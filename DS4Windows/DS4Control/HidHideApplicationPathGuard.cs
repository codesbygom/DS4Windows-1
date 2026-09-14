using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DS4WinWPF.DS4Control
{
    /// <summary>
    /// Inspects the named file itself, without activating or resolving an app
    /// execution alias. A failed inspection is not evidence of an alias.
    /// This metadata check does not validate the file's executable image.
    /// </summary>
    internal static class HidHideApplicationPathGuard
    {
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const uint IoReparseTagAppExecLink = 0x8000001B;
        private const int ErrorInvalidName = 123;
        private const int ErrorDirectory = 267;
        private const string DeviceVolumePrefix = @"\Device\HarddiskVolume";

        internal delegate bool MetadataReader(string nativePath,
            out uint fileAttributes, out uint reparseTag, out int win32Error);

        internal static bool TryInspect(string path,
            out bool isAppExecutionAlias, out int win32Error)
        {
            return TryInspect(path, TryReadMetadata,
                out isAppExecutionAlias, out win32Error);
        }

        internal static bool TryInspect(string path, MetadataReader readMetadata,
            out bool isAppExecutionAlias, out int win32Error)
        {
            isAppExecutionAlias = false;
            win32Error = ErrorInvalidName;
            if (!TryGetNativePath(path, out string nativePath))
            {
                return false;
            }

            if (!readMetadata(nativePath, out uint attributes, out uint tag,
                    out win32Error))
            {
                return false;
            }

            if ((attributes & FileAttributeDirectory) != 0)
            {
                win32Error = ErrorDirectory;
                return false;
            }

            isAppExecutionAlias =
                (attributes & FileAttributeReparsePoint) != 0 &&
                tag == IoReparseTagAppExecLink;
            win32Error = 0;
            return true;
        }

        private static bool TryGetNativePath(string path, out string nativePath)
        {
            nativePath = null;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            // Only DOS drive paths and HidHide's disk-volume names are admitted.
            // Never pass caller-supplied GLOBALROOT, UNC, or arbitrary device
            // names to CreateFile, which can otherwise open more than files.
            string normalized = path.Replace('/', '\\');
            int tailStart;
            string prefix;
            if (normalized.Length > 3 && IsAsciiLetter(normalized[0]) &&
                normalized[1] == ':' && normalized[2] == '\\')
            {
                tailStart = 3;
                prefix = @"\\?\";
            }
            else if (normalized.StartsWith(DeviceVolumePrefix,
                         StringComparison.OrdinalIgnoreCase))
            {
                tailStart = DeviceVolumePrefix.Length;
                int numberStart = tailStart;
                while (tailStart < normalized.Length &&
                       normalized[tailStart] >= '0' &&
                       normalized[tailStart] <= '9')
                {
                    tailStart++;
                }

                if (tailStart == numberStart || tailStart >= normalized.Length ||
                    normalized[tailStart++] != '\\')
                {
                    return false;
                }

                prefix = @"\\?\GLOBALROOT";
            }
            else
            {
                return false;
            }

            if (tailStart >= normalized.Length)
            {
                return false;
            }

            // Extended paths deliberately bypass Win32 normalization. Reject
            // ambiguous components and alternate data streams instead of
            // inspecting a different name from the one HidHide will store.
            foreach (string component in normalized.Substring(tailStart).Split('\\'))
            {
                if (component.Length == 0 || component == "." || component == ".." ||
                    component.EndsWith(".", StringComparison.Ordinal) ||
                    component.EndsWith(" ", StringComparison.Ordinal))
                {
                    return false;
                }

                foreach (char value in component)
                {
                    if (value < ' ' || value == ':' || value == '"' ||
                        value == '<' || value == '>' || value == '|' ||
                        value == '?' || value == '*')
                    {
                        return false;
                    }
                }
            }

            nativePath = prefix + normalized;
            return true;
        }

        private static bool IsAsciiLetter(char value)
        {
            return (value >= 'A' && value <= 'Z') ||
                (value >= 'a' && value <= 'z');
        }

        private static bool TryReadMetadata(string nativePath,
            out uint fileAttributes, out uint reparseTag, out int win32Error)
        {
            const uint fileReadAttributes = 0x00000080;
            const uint shareReadWriteDelete = 0x00000007;
            const uint openExisting = 3;
            const uint openReparsePoint = 0x00200000;
            const uint backupSemantics = 0x02000000;
            const int fileAttributeTagInfo = 9;

            fileAttributes = 0;
            reparseTag = 0;
            using SafeFileHandle handle = CreateFileW(nativePath,
                fileReadAttributes, shareReadWriteDelete, IntPtr.Zero,
                openExisting, openReparsePoint | backupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }

            if (!GetFileInformationByHandleEx(handle, fileAttributeTagInfo,
                    out FileAttributeTagInformation information,
                    (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }

            fileAttributes = information.FileAttributes;
            reparseTag = information.ReparseTag;
            win32Error = 0;
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInformation
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string fileName,
            uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file, int informationClass,
            out FileAttributeTagInformation information, uint bufferSize);
    }
}
