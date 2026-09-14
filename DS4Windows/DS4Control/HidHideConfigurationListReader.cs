using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DS4Windows;

namespace DS4WinWPF.DS4Control
{
    /// <summary>
    /// Strict reads for the cold configuration-repair workflow. Existing input
    /// and controller-lifecycle getters do not use this reader.
    /// </summary>
    internal static class HidHideConfigurationListReader
    {
        internal const int MaximumBytes = 1024 * 1024;

        internal delegate bool ReadControl(IntPtr handle, uint controlCode,
            IntPtr output, int outputLength, out int bytesReturned);

        internal static bool TryRead(SafeHandle handle, uint controlCode,
            out List<string> entries) =>
            TryRead(handle, controlCode, ReadNative, out entries);

        internal static bool TryRead(SafeHandle handle, uint controlCode,
            ReadControl read, out List<string> entries)
        {
            entries = new List<string>();
            if (handle == null || handle.IsClosed || handle.IsInvalid || read == null)
                return false;

            bool acquired = false;
            try
            {
                // Both IOCTLs must refer to the same live control handle even
                // if its owner concurrently requests disposal.
                handle.DangerousAddRef(ref acquired);
                IntPtr nativeHandle = handle.DangerousGetHandle();
                if (!read(nativeHandle, controlCode, IntPtr.Zero, 0,
                        out int requiredBytes) || !IsValidLength(requiredBytes))
                    return false;

                byte[] data = new byte[requiredBytes];
                // A short driver write must not acquire a synthetic terminator
                // from freshly zero-initialized memory.
                Array.Fill(data, byte.MaxValue);
                GCHandle pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
                try
                {
                    if (!read(nativeHandle, controlCode, pinned.AddrOfPinnedObject(),
                            data.Length, out int bytesReturned) ||
                        !IsValidLength(bytesReturned) || bytesReturned > data.Length)
                        return false;
                    return TryParse(data.AsSpan(0, bytesReturned), out entries);
                }
                finally
                {
                    pinned.Free();
                }
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            finally
            {
                if (acquired) handle.DangerousRelease();
            }
        }

        internal static bool TryParse(ReadOnlySpan<byte> data,
            out List<string> entries)
        {
            entries = new List<string>();
            if (!IsValidLength(data.Length)) return false;

            // Preserve exact UTF-16 code units, including unusual file names;
            // a replacement-fallback decoder could silently change a path.
            char[] characters = new char[data.Length / 2];
            for (int index = 0; index < characters.Length; index++)
                characters[index] = (char)BinaryPrimitives.ReadUInt16LittleEndian(
                    data.Slice(index * 2, 2));

            // Released HidHide drivers return just one NUL for an empty list.
            if (characters[0] == '\0')
                return characters.Length == 1 || characters[1] == '\0';

            var parsed = new List<string>();
            int start = 0;
            for (int index = 0; index < characters.Length; index++)
            {
                if (characters[index] != '\0') continue;
                if (index == start)
                {
                    // HidHide 1.5 can report an oversized buffer. Only the
                    // first MULTI_SZ terminator ends the list; ignore its tail.
                    entries = parsed;
                    return true;
                }
                parsed.Add(new string(characters, start, index - start));
                start = index + 1;
            }
            return false;
        }

        private static bool IsValidLength(int count) =>
            count >= 2 && count <= MaximumBytes && (count & 1) == 0;

        private static bool ReadNative(IntPtr handle, uint controlCode,
            IntPtr output, int outputLength, out int bytesReturned)
        {
            bytesReturned = 0;
            return NativeMethods.DeviceIoControl(handle, controlCode,
                IntPtr.Zero, 0, output, outputLength, ref bytesReturned, IntPtr.Zero);
        }
    }
}
