using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;

namespace DS4Windows.Installation;

internal readonly record struct StartupSetupState(bool Requested,
    string DeferredReason, string BootSessionId);

// Intent is separate from Scheduler state: setup can temporarily suspend the
// original tasks without recording an opt-out on the user's behalf.
internal static class StartupSetupStore
{
    internal const string MachinePath = @"SOFTWARE\DS4Windows\StartupSetup";
    internal const string UserPath = @"Software\DS4Windows";

    internal static StartupSetupState? Read(string sid)
    {
        ValidateSid(sid);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(MachinePath + "\\" + sid);
        if (key?.GetValue("Requested") is not int requested || (requested != 0 && requested != 1)) return null;
        string reason = key.GetValue("DeferredReason") as string ?? "";
        if (reason == "RestartRequired" && key.GetValue("Resume") is string json && json.Length <= 16384)
        {
            var resume = JsonSerializer.Deserialize<StartupSetupResume>(json);
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            using var user = users.OpenSubKey(sid + "\\" + UserPath);
            if (resume != null && string.Equals(user?.GetValue("SetupResumeAttempt") as string,
                    resume.Id, StringComparison.Ordinal)) reason = "RepairRequired";
        }
        return new StartupSetupState(requested == 1, reason, key.GetValue("BootSessionId") as string ?? "");
    }

    internal static bool? ReadUserPreference(string sid)
    {
        ValidateSid(sid);
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        using var key = users.OpenSubKey(sid + "\\" + UserPath);
        return key?.GetValue("RunAtStartupRequested") is int value && (value == 0 || value == 1)
            ? value == 1 : null;
    }

    internal static void WriteCurrentUserPreference(bool enabled)
    {
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = user.CreateSubKey(UserPath, true);
        key.SetValue("RunAtStartupRequested", enabled ? 1 : 0, RegistryValueKind.DWord);
        key.Flush();
        if (!Equals(key.GetValue("RunAtStartupRequested"), enabled ? 1 : 0))
            throw new IOException("Windows did not save the startup preference.");
    }

    internal static void ValidateSid(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid) ||
            !string.Equals(new SecurityIdentifier(sid).Value, sid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The setup account SID is invalid.");
    }
}

internal sealed class StartupSetupResume
{
    public string Id { get; set; }
    public string SnapshotId { get; set; }
    public string BundleId { get; set; }
    public string Kind { get; set; }
    public string TargetSid { get; set; }
    public string TargetName { get; set; }
    public string TargetLocalAppData { get; set; }
    public string TargetRoamingAppData { get; set; }
    public string TargetExecutable { get; set; }
    public string Executable { get; set; }
    public string ExecutableSha256 { get; set; }
    public string BootSessionId { get; set; }
    public bool Portable { get; set; }
    public bool StartupRequested { get; set; }
}

internal static class StartupSetupRecovery
{
    internal const string ResumeArgument = "--resume-startup-setup";
    internal const string ShortcutName = "DS4Windows Setup Resume.lnk";

    internal static string BootSessionId()
    {
        using var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            using (item)
                return ManagementDateTimeConverter.ToDateTime((string)item["LastBootUpTime"])
                    .ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        }
        throw new IOException("Windows did not report its boot session.");
    }

    internal static bool IsEligible(StartupSetupResume pending, string id, string sid, string boot,
        string previousAttempt) => pending != null &&
        Guid.TryParseExact(id, "N", out _) && string.Equals(pending.Id, id, StringComparison.Ordinal) &&
        string.Equals(pending.TargetSid, sid, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(pending.BootSessionId) && !string.IsNullOrWhiteSpace(boot) &&
        !string.Equals(pending.BootSessionId, boot, StringComparison.Ordinal) &&
        !string.Equals(previousAttempt, id, StringComparison.Ordinal);

    internal static string NativeProgramFiles
    {
        get
        {
            // A process environment variable is not authority to retarget an
            // elevated snapshot. The machine's native registry view is stable
            // for both 32-bit and 64-bit callers.
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion");
            string path = key?.GetValue("ProgramFilesDir") as string;
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("Windows did not provide its native Program Files directory.");
            return Path.GetFullPath(path);
        }
    }

    internal static string ExpectedExecutable(StartupSetupResume pending) => pending.Kind switch
    {
        "bundle" => Path.Combine(NativeProgramFiles, "DS4Windows.Setup", pending.SnapshotId,
            "bundle", "DS4Windows_Setup_x64.exe"),
        "embedded" => Path.Combine(NativeProgramFiles, "DS4Windows.Setup", pending.SnapshotId,
            "package", "DS4Windows.exe"),
        _ => throw new InvalidDataException("The setup resume kind is invalid."),
    };

    internal static string ShortcutPath(StartupSetupResume pending) =>
        Path.Combine(pending.TargetRoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup", ShortcutName);

    internal static void RequirePlainPath(string path)
    {
        string full = Path.GetFullPath(path);
        for (string part = full; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            if ((Directory.Exists(part) || File.Exists(part)) &&
                (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A setup resume path is a filesystem link.");
    }

    internal static void Validate(StartupSetupResume pending)
    {
        if (pending == null || !Guid.TryParseExact(pending.Id, "N", out _))
            throw new InvalidDataException("The setup resume identifier is invalid.");
        if (!Guid.TryParseExact(pending.SnapshotId, "N", out _))
            throw new InvalidDataException("The protected setup snapshot identifier is invalid.");
        if (pending.Kind == "bundle" && !Guid.TryParseExact(pending.BundleId, "B", out _))
            throw new InvalidDataException("The setup resume bundle identifier is invalid.");
        StartupSetupStore.ValidateSid(pending.TargetSid);
        if (!SamePath(pending.Executable, ExpectedExecutable(pending)))
            throw new InvalidDataException("Setup resume must use its protected package snapshot.");
        foreach (string path in new[] { pending.Executable, pending.TargetExecutable,
                     pending.TargetLocalAppData, pending.TargetRoamingAppData })
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new InvalidDataException("The setup resume path is invalid.");
            RequirePlainPath(path);
        }
        if (string.IsNullOrWhiteSpace(pending.TargetName) || string.IsNullOrWhiteSpace(pending.BootSessionId))
            throw new InvalidDataException("The setup resume account or boot session is missing.");
    }

    internal static bool SamePath(string first, string second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
        string.Equals(Path.GetFullPath(first).TrimEnd('\\'), Path.GetFullPath(second).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    internal static StartupSetupResume Read(string sid)
    {
        StartupSetupStore.ValidateSid(sid);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(StartupSetupStore.MachinePath + "\\" + sid);
        string json = key?.GetValue("Resume") as string;
        if (string.IsNullOrWhiteSpace(json)) return null;
        if (json.Length > 16384) throw new InvalidDataException("The setup resume record is too large.");
        var pending = JsonSerializer.Deserialize<StartupSetupResume>(json);
        Validate(pending);
        if (!string.Equals(pending.TargetSid, sid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The setup resume account changed.");
        return pending;
    }

    internal static void Register(StartupSetupResume pending)
    {
        Validate(pending);
        pending.ExecutableSha256 = FileHash(pending.Executable);
        string shortcutPath = ShortcutPath(pending);
        RequirePlainPath(shortcutPath);
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));
        object shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8")));
        try
        {
            dynamic shortcut = ((dynamic)shell).CreateShortcut(shortcutPath);
            try
            {
                // A matching filename does not authorize overwriting a user's
                // unrelated shortcut. An older verified resume may be replaced.
                if (File.Exists(shortcutPath) && !IsOwnedShortcut(shortcut, Read(pending.TargetSid)) &&
                    !(pending.Kind == "bundle" && SamePath((string)shortcut.TargetPath, pending.Executable) &&
                      string.Equals((string)shortcut.Arguments, "/repair", StringComparison.Ordinal)))
                    throw new IOException("The setup resume shortcut belongs to another configuration.");
                shortcut.TargetPath = pending.Executable;
                shortcut.Arguments = Arguments(pending);
                shortcut.WorkingDirectory = Path.GetDirectoryName(pending.Executable);
                shortcut.Description = "DS4Windows verified setup resume v1";
                shortcut.Save();
                if (!IsOwnedShortcut(shortcut, pending))
                    throw new IOException("Windows did not save the verified setup resume shortcut.");
            }
            finally { Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.CreateSubKey(StartupSetupStore.MachinePath + "\\" + pending.TargetSid, true);
        key.SetValue("Resume", JsonSerializer.Serialize(pending), RegistryValueKind.String);
        key.Flush();
    }

    private static string Arguments(StartupSetupResume pending) =>
        (pending.Kind == "bundle" ? "/repair " : "") + ResumeArgument + " " + pending.Id;

    private static bool IsOwnedShortcut(dynamic shortcut, StartupSetupResume pending) => pending != null &&
        SamePath((string)shortcut.TargetPath, pending.Executable) &&
        string.Equals((string)shortcut.Arguments, Arguments(pending), StringComparison.Ordinal) &&
        SamePath((string)shortcut.WorkingDirectory, Path.GetDirectoryName(pending.Executable));

    internal static void RemoveShortcut(StartupSetupResume pending)
    {
        string path = ShortcutPath(pending);
        RequirePlainPath(path);
        if (!File.Exists(path)) return;
        object shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8")));
        try
        {
            dynamic shortcut = ((dynamic)shell).CreateShortcut(path);
            try
            {
                if (!IsOwnedShortcut(shortcut, pending)) return;
            }
            finally { Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        File.Delete(path);
    }

    internal static bool IsOwnedLegacyShortcut(string markerPath, string target, string arguments,
        string workingDirectory, string expectedExecutable)
    {
        if (string.IsNullOrWhiteSpace(markerPath) || !Path.IsPathFullyQualified(markerPath)) return false;
        string fullPath = Path.GetFullPath(markerPath);
        string startupSuffix = Path.Combine("Microsoft", "Windows", "Start Menu", "Programs", "Startup", ShortcutName);
        return fullPath.EndsWith("\\" + startupSuffix, StringComparison.OrdinalIgnoreCase) &&
            SamePath(target, expectedExecutable) && string.Equals(arguments, "/repair", StringComparison.Ordinal) &&
            SamePath(workingDirectory, Path.GetDirectoryName(expectedExecutable));
    }

    // True means the recorded shortcut is absent or was removed. False leaves
    // both a changed shortcut and its old cache intact rather than breaking a
    // link whose current ownership cannot be established.
    internal static bool RemoveLegacyShortcut(string markerPath, string expectedExecutable)
    {
        if (markerPath == null) return true;
        if (!IsOwnedLegacyShortcut(markerPath, expectedExecutable, "/repair",
                Path.GetDirectoryName(expectedExecutable), expectedExecutable)) return false;
        RequirePlainPath(markerPath);
        RequirePlainPath(expectedExecutable);
        if (!File.Exists(markerPath)) return true;
        object shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8")));
        try
        {
            dynamic shortcut = ((dynamic)shell).CreateShortcut(markerPath);
            try
            {
                if (!IsOwnedLegacyShortcut(markerPath, (string)shortcut.TargetPath, (string)shortcut.Arguments,
                        (string)shortcut.WorkingDirectory, expectedExecutable)) return false;
            }
            finally { Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        File.Delete(markerPath);
        return true;
    }

    internal static bool MatchesBundle(StartupSetupResume pending, string bundleId) => pending != null &&
        pending.Kind == "bundle" && Guid.TryParseExact(bundleId, "B", out _) &&
        string.Equals(pending.BundleId, bundleId, StringComparison.OrdinalIgnoreCase);

    internal static bool TryClaimBundle(string id, string bundleId, out StartupSetupResume pending) =>
        TryClaim(id, null, out pending, bundleId);

    internal static bool TryClaim(string id, string executable, out StartupSetupResume pending, string bundleId = null)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value;
        using var mutex = new Mutex(false, @"Global\DS4Windows-SetupResume-" + sid);
        bool ownsMutex;
        try { ownsMutex = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex) { pending = null; return false; }
        try { return ClaimLocked(id, executable, sid, bundleId, out pending); }
        finally { mutex.ReleaseMutex(); }
    }

    private static bool ClaimLocked(string id, string executable, string sid, string bundleId, out StartupSetupResume pending)
    {
        pending = Read(sid);
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = user.CreateSubKey(StartupSetupStore.UserPath, true);
        string previousAttempt = key.GetValue("SetupResumeAttempt") as string;
        var captured = pending;
        return Claim(pending, id, sid, BootSessionId(), previousAttempt,
            // Turning off logon startup does not cancel the driver setup the
            // user already requested. The installer rereads that preference
            // before registration and removes the original tasks when off.
            false,
            () =>
            {
                if (bundleId != null)
                {
                    if (!MatchesBundle(captured, bundleId))
                        throw new IOException("The setup resume belongs to another installer bundle.");
                    // Burn persists OriginalSource across runs and extracts
                    // the BA, so neither is a current outer-bundle path. Bind
                    // to its immutable provider ID and the protected snapshot.
                    VerifyExecutable(captured, captured.Executable);
                }
                else
                {
                    if (captured.Kind != "embedded")
                        throw new IOException("This setup resume requires its installer bundle.");
                    VerifyExecutable(captured, executable);
                }
            },
            () => { key.SetValue("SetupResumeAttempt", captured.Id, RegistryValueKind.String); key.Flush(); },
            () => RemoveShortcut(captured));
    }

    internal static bool Claim(StartupSetupResume pending, string id, string sid, string boot,
        string previousAttempt, bool canceled, Action verify, Action markAttempt, Action removeShortcut)
    {
        if (!IsEligible(pending, id, sid, boot, previousAttempt)) return false;
        verify();
        // One automatic attempt per staged transaction, including UAC cancel.
        // A second required reboot stages a new transaction. Failed resumes
        // remain visible as pending setup and can be retried explicitly.
        markAttempt();
        try { removeShortcut(); }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is COMException)
        {
            System.Diagnostics.Trace.TraceWarning("Setup resume shortcut cleanup failed: " + error.Message);
        }
        return !canceled;
    }

    internal static void VerifyExecutable(StartupSetupResume pending, string executable)
    {
        Validate(pending);
        RequireProtectedPath(pending.Executable, NativeProgramFiles);
        if (!SamePath(executable, pending.Executable) || !string.Equals(FileHash(pending.Executable),
                pending.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The protected setup resume executable changed.");
    }

    internal static void ProtectSnapshot(string directory, string sid)
    {
        StartupSetupStore.ValidateSid(sid);
        RequirePlainPath(directory);
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(true, false);
        foreach (var owner in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
        RequireProtectedPath(directory, NativeProgramFiles);
    }

    internal static string EnsureProtectedStagingRoot(string sid)
    {
        StartupSetupStore.ValidateSid(sid);
        string programFiles = NativeProgramFiles;
        RequireProtectedPath(programFiles, programFiles);
        string root = Path.Combine(programFiles, "DS4Windows.Setup");
        if (!Directory.Exists(root))
        {
            if (File.Exists(root)) throw new IOException("The protected setup directory is a file.");
            Directory.CreateDirectory(root);
            // The base permits traversal for any account; each fresh snapshot
            // grants read/execute only to its own target account and admins.
            ProtectSnapshot(root, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value);
        }
        RequireProtectedPath(root, programFiles);
        return root;
    }

    internal static void RequireProtectedPath(string path, string protectedRoot)
    {
        string source = Path.GetFullPath(path);
        string root = Path.GetFullPath(protectedRoot).TrimEnd('\\');
        if (!SamePath(source, root) && !source.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The setup source is outside its protected root.");
        for (string current = source; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The setup source traverses a filesystem link.");
            bool directory = (attributes & FileAttributes.Directory) != 0;
            if (current != source && !directory)
                throw new IOException("The setup source parent is not a directory.");
            FileSystemSecurity security = directory
                ? new DirectoryInfo(current).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : new FileInfo(current).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            bool ancestor = !SamePath(current, root) && !current.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
            RequireProtectedSecurity(security.GetSecurityDescriptorBinaryForm(), ancestor);
        }
    }

    internal static void RequireProtectedSecurity(byte[] descriptor, bool ancestor)
    {
        const int genericAll = 0x10000000, genericWrite = 0x40000000;
        const int controlMutation = 0x10000 | 0x40000 | 0x80000; // DELETE, WRITE_DAC, WRITE_OWNER.
        var security = new RawSecurityDescriptor(descriptor, 0);
        if (!IsTrustedOwner(security.Owner) || security.DiscretionaryAcl == null)
            throw new IOException("The setup source is not owned and protected by Windows administrators.");
        int forbidden = controlMutation | genericAll | (int)FileSystemRights.DeleteSubdirectoriesAndFiles;
        if (!ancestor) forbidden |= (int)(FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes) | genericWrite;
        foreach (GenericAce ace in security.DiscretionaryAcl)
        {
            if ((ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (ace is not CommonAce entry || entry.IsCallback)
                throw new IOException("The setup source has an unsupported access-control entry.");
            if (entry.AceQualifier == AceQualifier.AccessAllowed && !IsTrustedOwner(entry.SecurityIdentifier) &&
                (entry.AccessMask & forbidden) != 0)
                throw new IOException("An ordinary account can modify or replace the setup source.");
        }
    }

    private static bool IsTrustedOwner(SecurityIdentifier sid) => sid != null &&
        (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
         sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"); // TrustedInstaller.

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void Clear(string sid)
    {
        StartupSetupResume pending = null;
        try { pending = Read(sid); }
        catch (Exception error) when (error is ArgumentException || error is JsonException ||
            error is InvalidDataException || error is IOException || error is UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Preserved unverified setup resume shortcut: " + error.Message);
        }
        if (pending != null)
        {
            try { RemoveShortcut(pending); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is COMException)
            {
                System.Diagnostics.Trace.TraceWarning("Setup resume shortcut cleanup failed: " + error.Message);
            }
        }
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(StartupSetupStore.MachinePath + "\\" + sid, true);
        key?.DeleteValue("Resume", false);
    }

    internal static void ClearAll()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(StartupSetupStore.MachinePath);
        if (key == null) return;
        foreach (string sid in key.GetSubKeyNames())
        {
            try { Clear(sid); }
            catch (ArgumentException error)
            {
                System.Diagnostics.Trace.TraceWarning("Preserved invalid setup account marker: " + error.Message);
            }
        }
    }
}
