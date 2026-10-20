using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PCLDownloadFix
{
    /// <summary>
    /// File replacement helpers. The primary path uses File.Replace, which keeps the
    /// destination ACL and is atomic on NTFS. If that is unavailable we fall back to
    /// MoveFileEx with MOVEFILE_REPLACE_EXISTING, and only then to copy + delete.
    /// </summary>
    public static class FileOps
    {
        private const uint MoveFileReplaceExisting = 0x1;
        private const uint MoveFileWriteThrough = 0x8;

        public static void Replace(string source, string destination)
        {
            if (!File.Exists(source))
                throw new FileNotFoundException("找不到待写入的临时文件。", source);

            var destinationExists = File.Exists(destination);
            var originalAttributes = destinationExists ? File.GetAttributes(destination) : (FileAttributes?)null;

            try
            {
                if (destinationExists && (originalAttributes.Value & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(destination, originalAttributes.Value & ~FileAttributes.ReadOnly);

                if (destinationExists)
                {
                    try
                    {
                        File.Replace(source, destination, null, true);
                        return;
                    }
                    catch (IOException)
                    {
                        // Fall through to the Win32 replacement, then to a copy.
                    }
                    catch (PlatformNotSupportedException)
                    {
                    }

                    if (TryMoveFileEx(source, destination, MoveFileReplaceExisting | MoveFileWriteThrough))
                        return;

                    File.Copy(source, destination, true);
                    File.Delete(source);
                    return;
                }

                File.Move(source, destination);
            }
            finally
            {
                if (originalAttributes.HasValue && File.Exists(destination))
                {
                    try { File.SetAttributes(destination, originalAttributes.Value); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static bool TryMoveFileEx(string existingFileName, string newFileName, uint flags)
        {
            try
            {
                return MoveFileEx(existingFileName, newFileName, flags);
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
    }
}
