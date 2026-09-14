using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;

namespace DS4Windows.Installation;

// Burn caches and registers its own engine before executing the package chain.
// Registry values are only routing hints: the current engine's immutable bundle
// ID and the protected matching cache directory establish execution authority.
// The original download can have changed since UAC and is not a trusted source.
internal static class SetupResumeBundleSource
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string BundleTag = "DS4WindowsManagedV2";
    internal const string UpgradeCode = "BC70CCB1-AD65-42A0-B468-8A7278A37A62";

    internal static string Resolve(string bundleId)
    {
        string id = NormalizeBundleId(bundleId);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        string registrationPath = UninstallPath + "\\" + id;
        using var registration = machine.OpenSubKey(registrationPath) ??
            throw new InvalidDataException("The current Burn bundle has no machine cache registration.");
        // Windows' default Uninstall ACL includes Terminal Server User writes.
        // Never authorize code using this metadata alone, even for a matching
        // provider/tag. No value can select a path outside the exact current ID.
        string cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache");
        string source = ValidateRegistration(id,
            registration.GetValue("BundleProviderKey") as string,
            registration.GetValue("BundleTag") as string,
            registration.GetValue("BundleUpgradeCode") as string[],
            registration.GetValue("BundleCachePath") as string, cacheRoot);
        // Reject links and mutable ownership from the cached file through its
        // ancestors. ProgramData may allow new sibling directories, but must
        // not allow an ordinary user to delete or replace this cache root.
        if (Directory.Exists(source)) throw new IOException("The cached bundle executable is a directory.");
        StartupSetupRecovery.RequireProtectedPath(source, cacheRoot);
        return source;
    }

    internal static string ValidateRegistration(string bundleId, string providerKey, string tag,
        string[] upgradeCodes, string cachePath, string cacheRoot)
    {
        string id = NormalizeBundleId(bundleId);
        if (!Guid.TryParse(providerKey, out Guid provider) || provider != Guid.Parse(id) ||
            !string.Equals(tag, BundleTag, StringComparison.Ordinal) || upgradeCodes == null ||
            !upgradeCodes.Any(value => Guid.TryParse(value, out Guid upgrade) && upgrade == Guid.Parse(UpgradeCode)))
            throw new InvalidDataException("The current cache registration does not identify this DS4Windows bundle.");
        if (string.IsNullOrWhiteSpace(cachePath) || !Path.IsPathFullyQualified(cachePath) ||
            cachePath.StartsWith(@"\\", StringComparison.Ordinal) || cachePath.Contains('/'))
            throw new InvalidDataException("The current bundle cache path is invalid.");
        string source = Path.GetFullPath(cachePath);
        string fileName = Path.GetFileName(source);
        if (!string.Equals(source, cachePath, StringComparison.OrdinalIgnoreCase) ||
            !SamePath(Path.GetDirectoryName(source), Path.Combine(cacheRoot, id)) ||
            !fileName.StartsWith("DS4Windows_", StringComparison.Ordinal) ||
            !fileName.EndsWith("_Setup_x64.exe", StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Setup resume must use the exact current bundle in the protected machine cache.");
        return source;
    }

    private static string NormalizeBundleId(string value) => Guid.TryParseExact(value, "B", out Guid id)
        ? id.ToString("B").ToUpperInvariant() : throw new InvalidDataException("The current bundle identifier is invalid.");

    private static bool SamePath(string first, string second) =>
        string.Equals(Path.GetFullPath(first).TrimEnd('\\'), Path.GetFullPath(second).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
}
