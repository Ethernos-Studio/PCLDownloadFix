using System;
using System.Diagnostics;
using System.IO;

namespace PCLDownloadFix
{
    public static class PclProcess
    {
        public static bool IsRunning(string executablePath)
        {
            var full = Path.GetFullPath(executablePath);
            var expectedName = Path.GetFileNameWithoutExtension(full);

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    if (!string.Equals(name, "Plain Craft Launcher 2", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string path = null;
                    try { path = process.MainModule == null ? null : process.MainModule.FileName; }
                    catch { }
                    if (path != null && string.Equals(Path.GetFullPath(path), full, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // Access denied for some system processes; ignore them.
                }
            }
            return false;
        }

        public static bool IsFileLocked(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // A read-only attribute is not a lock; ReplaceFile clears it before writing.
                return false;
            }
        }

        public static void EnsureNotRunning(string path)
        {
            if (IsRunning(path))
                throw new PatchException("PCL 正在运行，请先完全关闭启动器后再操作。");
            if (IsFileLocked(path))
                throw new PatchException("目标文件正被占用，请关闭 PCL 或其他占用该文件的程序后重试。");
        }
    }
}
