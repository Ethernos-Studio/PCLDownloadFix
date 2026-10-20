using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace PCLDownloadFix
{
    [DataContract]
    public sealed class PatchManifest
    {
        [DataMember(Name = "schema", Order = 0)]
        public int Schema { get; set; }

        [DataMember(Name = "originalSha256", Order = 1)]
        public string OriginalSha256 { get; set; }

        [DataMember(Name = "patchedSha256", Order = 2)]
        public string PatchedSha256 { get; set; }

        [DataMember(Name = "backupFile", Order = 3)]
        public string BackupFile { get; set; }

        [DataMember(Name = "build", Order = 4)]
        public string Build { get; set; }

        [DataMember(Name = "toolVersion", Order = 5)]
        public string ToolVersion { get; set; }

        [DataMember(Name = "createdUtc", Order = 6)]
        public string CreatedUtc { get; set; }
    }

    /// <summary>
    /// Manages the original-PCL backup next to the target executable.
    ///
    /// The active backup always lives at the documented "&lt;exe&gt;.pcl-original.bak" path.
    /// When PCL updates itself, the old backup is archived as
    /// "&lt;exe&gt;.pcl-original.&lt;sha8&gt;.bak" instead of blocking the new patch. A small
    /// sidecar manifest lets restore pick the exact backup that matches the patched file.
    /// </summary>
    public static class BackupStore
    {
        public const string LegacySuffix = ".pcl-original.bak";
        public const string ArchivedInfix = ".pcl-original.";
        public const string ManifestSuffix = ".pclfix.json";

        private const int ManifestSchema = 1;
        private const int KeepArchivedBackups = 5;

        public static string LegacyPath(string targetPath)
        {
            return targetPath + LegacySuffix;
        }

        public static string ManifestPath(string targetPath)
        {
            return targetPath + ManifestSuffix;
        }

        public static string ArchivedPath(string targetPath, string sha256)
        {
            var shortHash = string.IsNullOrEmpty(sha256) || sha256.Length < 8 ? "unknown" : sha256.Substring(0, 8);
            return targetPath + ArchivedInfix + shortHash + ".bak";
        }

        public static PatchManifest ReadManifest(string targetPath)
        {
            var path = ManifestPath(targetPath);
            if (!File.Exists(path)) return null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var serializer = new DataContractJsonSerializer(typeof(PatchManifest));
                    return (PatchManifest)serializer.ReadObject(stream);
                }
            }
            catch
            {
                // A damaged manifest must never block patching or restoring; the backup
                // files themselves remain authoritative.
                return null;
            }
        }

        public static bool TryWriteManifest(string targetPath, PatchManifest manifest)
        {
            if (manifest == null) return false;
            manifest.Schema = ManifestSchema;
            var path = ManifestPath(targetPath);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(PatchManifest));
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    serializer.WriteObject(stream, manifest);
                FileOps.Replace(temp, path);
                return true;
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                return false;
            }
        }

        public static void DeleteManifest(string targetPath)
        {
            try
            {
                var path = ManifestPath(targetPath);
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Picks the backup that should be shown (and used for restore) for the current file.
        /// The manifest wins only when it describes exactly this patched file.
        /// </summary>
        public static string ResolveBackupPath(string targetPath, string currentSha256, out PatchManifest manifest)
        {
            manifest = ReadManifest(targetPath);

            if (manifest != null &&
                string.Equals(manifest.PatchedSha256, currentSha256, StringComparison.OrdinalIgnoreCase))
            {
                // An empty BackupFile explicitly records "patched without a backup".
                // Do not fall back to an older legacy backup in that case.
                if (string.IsNullOrEmpty(manifest.BackupFile)) return null;

                var candidate = Path.Combine(Path.GetDirectoryName(targetPath) ?? "",
                    Path.GetFileName(manifest.BackupFile));
                if (File.Exists(candidate)) return candidate;
            }

            var legacy = LegacyPath(targetPath);
            if (File.Exists(legacy)) return legacy;

            var archived = FindArchivedBackups(targetPath);
            if (archived.Count == 1) return archived[0];
            if (archived.Count > 1)
            {
                // Without a manifest there is no way to know which one belongs to this
                // file; prefer the most recent and let restore's version check refuse a
                // clearly wrong build.
                return archived.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }

            return null;
        }

        /// <summary>
        /// Creates the active backup, archiving a previous one when the target has been
        /// replaced by a PCL update. Returns the path of the active backup.
        /// </summary>
        public static string EnsureOriginalBackup(string targetPath, string expectedHash, Action<LogEntry> log)
        {
            var legacy = LegacyPath(targetPath);
            if (File.Exists(legacy))
            {
                var existingHash = HashUtil.Sha256File(legacy);
                if (string.Equals(existingHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Write(log, LogLevel.Info, "原版备份已存在：" + legacy);
                    return legacy;
                }

                if (!IsValidOriginal(legacy))
                    throw new PatchException("备份文件已存在，但不是有效的原版 PCL，未覆盖任何文件：\r\n" + legacy);

                ArchiveExistingBackup(targetPath, legacy, existingHash, log);
            }

            Log.Write(log, LogLevel.Step, "创建原版备份...");
            var tempBackup = legacy + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(targetPath, tempBackup, true);
                var hash = HashUtil.Sha256File(tempBackup);
                if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new PatchException("创建备份时校验失败，已中止操作。");
                File.Move(tempBackup, legacy);
                Log.Write(log, LogLevel.Success, "原版备份：" + legacy);
            }
            finally
            {
                if (File.Exists(tempBackup))
                {
                    try { File.Delete(tempBackup); } catch { }
                }
            }

            PruneArchivedBackups(targetPath, log);
            return legacy;
        }

        public static IList<string> FindArchivedBackups(string targetPath)
        {
            try
            {
                var directory = Path.GetDirectoryName(targetPath);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return new List<string>();
                var pattern = Path.GetFileName(targetPath) + ArchivedInfix + "*.bak";
                return Directory.GetFiles(directory, pattern)
                    .Where(path => !string.Equals(path, LegacyPath(targetPath), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        public static bool IsValidOriginal(string path)
        {
            try
            {
                using (var target = PclTarget.Load(path))
                {
                    string detail;
                    var state = target.GetState(out detail);
                    return state == PatchState.Original || state == PatchState.Unrestricted;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void ArchiveExistingBackup(string targetPath, string legacy, string existingHash,
            Action<LogEntry> log)
        {
            var archived = ArchivedPath(targetPath, existingHash);
            if (File.Exists(archived))
            {
                var archivedHash = HashUtil.Sha256File(archived);
                if (string.Equals(archivedHash, existingHash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(legacy);
                    Log.Write(log, LogLevel.Warn,
                        "检测到 PCL 已更新，旧备份已存在归档，未重复保存：" + Path.GetFileName(archived));
                    return;
                }

                archived = targetPath + ArchivedInfix + existingHash.Substring(0, 8) + "-" +
                           Guid.NewGuid().ToString("N").Substring(0, 6) + ".bak";
            }

            File.Move(legacy, archived);
            Log.Write(log, LogLevel.Warn,
                "检测到 PCL 已更新：旧版原版备份已归档为 " + Path.GetFileName(archived));
        }

        private static void PruneArchivedBackups(string targetPath, Action<LogEntry> log)
        {
            var archived = FindArchivedBackups(targetPath);
            if (archived.Count <= KeepArchivedBackups) return;

            foreach (var path in archived.Skip(KeepArchivedBackups))
            {
                try
                {
                    File.Delete(path);
                    Log.Write(log, LogLevel.Detail, "已清理旧归档备份：" + Path.GetFileName(path));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
