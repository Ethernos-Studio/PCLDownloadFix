using System;
using System.Diagnostics;
using System.IO;

namespace PCLDownloadFix
{
    /// <summary>
    /// Identifies PCL builds by their embedded version resource rather than by the
    /// file name. Renamed copies (正式版.exe, 样本 (1).exe, ...) keep the original
    /// ProductName / OriginalFilename, so they can still be found.
    /// </summary>
    public static class PclIdentity
    {
        private const string ProductMarker = "Plain Craft Launcher";
        private const string OriginalExecutable = "Plain Craft Launcher 2.exe";
        private const long MinimumSize = 500 * 1024;
        private const long MaximumSize = 100L * 1024 * 1024;

        public static bool IsPclExecutable(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(Path.GetFileName(path), OriginalExecutable, StringComparison.OrdinalIgnoreCase))
                return true;

            try
            {
                var info = new FileInfo(path);
                if (info.Length < MinimumSize || info.Length > MaximumSize) return false;

                var version = FileVersionInfo.GetVersionInfo(path);
                return Contains(version.OriginalFilename, OriginalExecutable) ||
                       Contains(version.InternalName, OriginalExecutable) ||
                       Contains(version.ProductName, ProductMarker) ||
                       Contains(version.FileDescription, ProductMarker);
            }
            catch
            {
                return false;
            }
        }

        private static bool Contains(string value, string marker)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
