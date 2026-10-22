using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace PCLDownloadFix
{
    public enum HmclPackageKind
    {
        Jar,
        Executable
    }

    public sealed class HmclInfo
    {
        public string Path { get; set; }
        public HmclPackageKind Kind { get; set; }
        public string Version { get; set; }
        public bool HasRegionGate { get; set; }
        public bool IsPatched { get; set; }
        public bool BackupExists { get; set; }
        public string BackupPath { get; set; }
        public string Error { get; set; }
        public bool Success { get { return string.IsNullOrEmpty(Error); } }
    }

    public sealed class HmclPatchResult
    {
        public bool Success { get; set; }
        public bool Changed { get; set; }
        public string Message { get; set; }
        public string BackupPath { get; set; }
        public HmclInfo Info { get; set; }
    }

    /// <summary>
    /// Patches HMCL's RegionUtils class. HMCL disables the download pages when the
    /// machine/network is considered Chinese Mainland (HMCL PR #7017). Replacing the
    /// small class with a no-op/return-false stub keeps the rest of HMCL untouched.
    ///
    /// Supports both the normal .jar and the official .exe, which is HMCLauncher.exe
    /// followed by the complete JAR payload.
    /// </summary>
    public static class HmclPatcher
    {
        public const string RegionClassEntry = "org/jackhuang/hmcl/util/RegionUtils.class";
        public const string BackupSuffix = ".hmcl-original.bak";
        private const string StubResource = "PCLDownloadFix.HmclRegionUtils.class";
        private static readonly object StubSync = new object();
        private static byte[] _stub;

        public static HmclInfo Inspect(string path)
        {
            var info = new HmclInfo { Path = path, BackupPath = path + BackupSuffix };
            try
            {
                var package = ReadPackage(path);
                info.Kind = package.Kind;
                info.Version = ReadVersion(package.JarBytes);
                info.BackupExists = File.Exists(info.BackupPath);
                using (var archive = new ZipArchive(new MemoryStream(package.JarBytes, false), ZipArchiveMode.Read))
                {
                    var entry = archive.GetEntry(RegionClassEntry);
                    info.HasRegionGate = entry != null;
                    if (entry != null)
                    {
                        using (var stream = entry.Open())
                        using (var memory = new MemoryStream())
                        {
                            stream.CopyTo(memory);
                            info.IsPatched = BytesEqual(memory.ToArray(), Stub);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }
            return info;
        }

        public static HmclPatchResult Patch(string path, bool createBackup = true)
        {
            using (var writeLock = TargetLock.Acquire(path, TimeSpan.FromSeconds(15)))
            {
                var info = Inspect(path);
                if (!info.Success)
                    return new HmclPatchResult { Success = false, Message = info.Error, Info = info };
                if (!info.HasRegionGate)
                    return new HmclPatchResult
                    {
                        Success = true,
                        Changed = false,
                        Message = "该 HMCL 版本没有地区下载限制，无需修复。",
                        Info = info
                    };
                if (info.IsPatched)
                    return new HmclPatchResult
                    {
                        Success = true,
                        Changed = false,
                        Message = "HMCL 已经修复。",
                        BackupPath = info.BackupExists ? info.BackupPath : null,
                        Info = info
                    };

                var package = ReadPackage(path);
                var patchedJar = RewriteJar(package.JarBytes, Stub);
                var output = package.Prefix == null
                    ? patchedJar
                    : Concat(package.Prefix, patchedJar);

                string backupPath = null;
                if (createBackup)
                {
                    backupPath = CreateBackup(path);
                }

                var temp = path + ".hmcl-new";
                try
                {
                    File.WriteAllBytes(temp, output);
                    FileOps.Replace(temp, path);
                }
                finally
                {
                    TryDelete(temp);
                }

                var resultInfo = Inspect(path);
                return new HmclPatchResult
                {
                    Success = resultInfo.Success && resultInfo.IsPatched,
                    Changed = true,
                    Message = "HMCL 地区下载限制已修复。",
                    BackupPath = backupPath,
                    Info = resultInfo
                };
            }
        }

        public static HmclPatchResult Restore(string path)
        {
            using (var writeLock = TargetLock.Acquire(path, TimeSpan.FromSeconds(15)))
            {
                var backup = path + BackupSuffix;
                if (!File.Exists(backup))
                    return new HmclPatchResult
                    {
                        Success = false,
                        Message = "没有找到 HMCL 原版备份：" + backup
                    };

                var temp = path + ".hmcl-restore";
                try
                {
                    File.Copy(backup, temp, true);
                    FileOps.Replace(temp, path);
                }
                finally
                {
                    TryDelete(temp);
                }

                var info = Inspect(path);
                return new HmclPatchResult
                {
                    Success = info.Success && !info.IsPatched,
                    Changed = true,
                    Message = "已恢复 HMCL 原版。",
                    BackupPath = backup,
                    Info = info
                };
            }
        }

        private static HmclPackage ReadPackage(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("找不到 HMCL 文件。", path);

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 4)
                throw new InvalidDataException("文件太小，不是有效的 HMCL JAR/EXE。");

            if (bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
                return new HmclPackage { Kind = HmclPackageKind.Jar, JarBytes = bytes };

            if (bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                throw new InvalidDataException("仅支持 HMCL 的 .jar 或官方 .exe。");

            var jarStart = FindAppendedJarStart(bytes);
            if (jarStart <= 0 || jarStart >= bytes.Length - 4)
                throw new InvalidDataException("未能在 HMCL EXE 中找到追加的 JAR 数据。");

            var prefix = new byte[(int)jarStart];
            Buffer.BlockCopy(bytes, 0, prefix, 0, prefix.Length);
            var jar = new byte[bytes.Length - (int)jarStart];
            Buffer.BlockCopy(bytes, (int)jarStart, jar, 0, jar.Length);
            return new HmclPackage { Kind = HmclPackageKind.Executable, Prefix = prefix, JarBytes = jar };
        }

        private static long FindAppendedJarStart(byte[] bytes)
        {
            // The official HMCL exe is HMCLauncher.exe + an ordinary JAR. Find
            // the EOCD, then work backwards through the central directory offset.
            var start = Math.Max(0, bytes.Length - 65557);
            for (var i = bytes.Length - 22; i >= start; i--)
            {
                if (bytes[i] != 0x50 || bytes[i + 1] != 0x4B ||
                    bytes[i + 2] != 0x05 || bytes[i + 3] != 0x06)
                    continue;

                var commentLength = BitConverter.ToUInt16(bytes, i + 20);
                if (i + 22 + commentLength > bytes.Length) continue;

                var cdSize = BitConverter.ToUInt32(bytes, i + 12);
                var cdOffset = BitConverter.ToUInt32(bytes, i + 16);
                if (cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF)
                    throw new InvalidDataException("不支持 ZIP64 格式的 HMCL EXE。");

                var jarStart = i - (long)cdSize - cdOffset;
                if (jarStart < 0 || jarStart + 4 > bytes.Length) continue;
                if (bytes[jarStart] != 0x50 || bytes[jarStart + 1] != 0x4B ||
                    bytes[jarStart + 2] != 0x03 || bytes[jarStart + 3] != 0x04)
                    continue;
                return jarStart;
            }
            return -1;
        }

        private static byte[] RewriteJar(byte[] jarBytes, byte[] stub)
        {
            using (var source = new ZipArchive(new MemoryStream(jarBytes, false), ZipArchiveMode.Read))
            using (var output = new MemoryStream())
            {
                using (var target = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var entry in source.Entries)
                    {
                        // HMCL's custom signature cannot remain valid after the class
                        // changes. Dropping it makes the self-check report "unsigned"
                        // instead of pretending the jar is intact.
                        if (string.Equals(entry.FullName, "META-INF/hmcl_signature",
                                StringComparison.OrdinalIgnoreCase))
                            continue;

                        var created = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                        created.LastWriteTime = entry.LastWriteTime;
                        created.ExternalAttributes = entry.ExternalAttributes;

                        if (string.Equals(entry.FullName, RegionClassEntry, StringComparison.Ordinal))
                        {
                            using (var stream = created.Open())
                                stream.Write(stub, 0, stub.Length);
                        }
                        else
                        {
                            using (var input = entry.Open())
                            using (var stream = created.Open())
                                input.CopyTo(stream);
                        }
                    }
                }
                return output.ToArray();
            }
        }

        private static string CreateBackup(string path)
        {
            var backup = path + BackupSuffix;
            if (File.Exists(backup))
            {
                // Never silently replace an earlier original. Keep it as a
                // timestamped archive and use the current unpatched file as the
                // fresh restore point.
                var archived = path + ".hmcl-original." +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                File.Move(backup, archived);
            }
            File.Copy(path, backup, true);
            return backup;
        }

        private static string ReadVersion(byte[] jarBytes)
        {
            try
            {
                using (var archive = new ZipArchive(new MemoryStream(jarBytes, false), ZipArchiveMode.Read))
                {
                    var entry = archive.GetEntry("META-INF/MANIFEST.MF");
                    if (entry == null) return null;
                    using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.StartsWith("Implementation-Version:", StringComparison.OrdinalIgnoreCase))
                                return line.Substring(line.IndexOf(':') + 1).Trim();
                        }
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static byte[] Stub
        {
            get
            {
                lock (StubSync)
                {
                    if (_stub != null) return _stub;
                    using (var stream = typeof(HmclPatcher).Assembly.GetManifestResourceStream(StubResource))
                    {
                        if (stream == null)
                            throw new InvalidOperationException("缺少 HMCL RegionUtils 替换类资源。");
                        using (var memory = new MemoryStream())
                        {
                            stream.CopyTo(memory);
                            _stub = memory.ToArray();
                        }
                    }
                    return _stub;
                }
            }
        }

        private static bool BytesEqual(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length) return false;
            for (var i = 0; i < first.Length; i++)
                if (first[i] != second[i]) return false;
            return true;
        }

        private static byte[] Concat(byte[] first, byte[] second)
        {
            var result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private sealed class HmclPackage
        {
            public HmclPackageKind Kind;
            public byte[] Prefix;
            public byte[] JarBytes;
        }
    }
}
