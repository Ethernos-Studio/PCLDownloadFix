using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PCLDownloadFix
{
    public sealed class PclScanProgress
    {
        public int Directories { get; set; }
        public int Found { get; set; }
        public string CurrentDirectory { get; set; }
    }

    /// <summary>
    /// Finds Plain Craft Launcher 2.exe copies without trying to interpret them.
    /// The scan is deliberately bounded: it skips system/package-cache directories,
    /// reparse points and very deep trees, and the caller can cancel it.
    /// </summary>
    public static class PclScanner
    {
        public const string ExecutableName = "Plain Craft Launcher 2.exe";
        private const int MaxResults = 500;

        private static readonly HashSet<string> SkippedDirectoryNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Windows",
                "$Recycle.Bin",
                "System Volume Information",
                "Recovery",
                "PerfLogs",
                "MSOCache",
                "Config.Msi",
                "ProgramData",
                "AppData",
                "node_modules",
                ".git",
                ".svn",
                "packages",
                "Temp"
            };

        public static IList<string> DefaultRoots()
        {
            var roots = new List<string>();
            AddRoot(roots, Directory.GetCurrentDirectory());
            AddRoot(roots, AppDomain.CurrentDomain.BaseDirectory);
            try { AddRoot(roots, Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory)); } catch { }

            AddSpecialFolder(roots, Environment.SpecialFolder.DesktopDirectory);
            AddSpecialFolder(roots, Environment.SpecialFolder.MyDocuments);
            AddSpecialFolder(roots, Environment.SpecialFolder.UserProfile);
            AddSpecialFolder(roots, Environment.SpecialFolder.ProgramFiles);
            AddSpecialFolder(roots, Environment.SpecialFolder.ProgramFilesX86);

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
                AddRoot(roots, Path.Combine(profile, "Downloads"));

            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
                    AddRoot(roots, drive.RootDirectory.FullName);
                }
            }
            catch
            {
            }

            return roots;
        }

        public static IList<string> Scan(IEnumerable<string> roots, int maxDepth, CancellationToken token,
            Action<PclScanProgress> progress)
        {
            var results = new List<string>();
            var seenResults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<KeyValuePair<string, int>>();
            var directories = 0;

            foreach (var root in roots ?? Enumerable.Empty<string>())
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(root)) continue;

                string full;
                try { full = Path.GetFullPath(root); }
                catch { continue; }

                if (File.Exists(full))
                {
                    if (IsPclFileName(full) && seenResults.Add(full))
                    {
                        results.Add(full);
                        Report(progress, directories, results.Count, Path.GetDirectoryName(full));
                    }
                    continue;
                }

                if (Directory.Exists(full) && visited.Add(full))
                    queue.Enqueue(new KeyValuePair<string, int>(full, 0));
            }

            while (queue.Count > 0 && results.Count < MaxResults)
            {
                token.ThrowIfCancellationRequested();
                var current = queue.Dequeue();
                directories++;
                if (directories % 20 == 0 || directories == 1)
                    Report(progress, directories, results.Count, current.Key);

                try
                {
                    foreach (var file in Directory.EnumerateFiles(current.Key, ExecutableName))
                    {
                        if (!IsPclFileName(file)) continue;
                        var full = Path.GetFullPath(file);
                        if (seenResults.Add(full)) results.Add(full);
                        if (results.Count >= MaxResults) break;
                    }
                }
                catch
                {
                    // Unreadable directory; keep scanning the rest.
                }

                if (current.Value >= maxDepth || results.Count >= MaxResults) continue;

                try
                {
                    foreach (var child in Directory.EnumerateDirectories(current.Key))
                    {
                        token.ThrowIfCancellationRequested();
                        string full;
                        try { full = Path.GetFullPath(child); }
                        catch { continue; }
                        if (!visited.Add(full)) continue;
                        if (ShouldSkipDirectory(full)) continue;
                        queue.Enqueue(new KeyValuePair<string, int>(full, current.Value + 1));
                    }
                }
                catch
                {
                }
            }

            Report(progress, directories, results.Count, null);
            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }

        public static IList<string> FindInDirectory(string directory, int maxDepth, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return new List<string>();
            return Scan(new[] { directory }, maxDepth, token, null);
        }

        public static bool IsPclFileName(string path)
        {
            return string.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase);
        }

        private static void Report(Action<PclScanProgress> progress, int directories, int found, string current)
        {
            if (progress == null) return;
            progress(new PclScanProgress
            {
                Directories = directories,
                Found = found,
                CurrentDirectory = current
            });
        }

        private static bool ShouldSkipDirectory(string path)
        {
            try
            {
                var name = Path.GetFileName(path);
                if (SkippedDirectoryNames.Contains(name)) return true;
                var attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return true;
            }
        }

        private static void AddSpecialFolder(List<string> roots, Environment.SpecialFolder folder)
        {
            try { AddRoot(roots, Environment.GetFolderPath(folder)); }
            catch { }
        }

        private static void AddRoot(List<string> roots, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path);
                if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
            }
            catch
            {
            }
        }
    }
}
