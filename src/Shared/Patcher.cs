using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace PCLDownloadFix
{
    public sealed class PatchOptions
    {
        public bool CreateBackup = true;
        public bool Verify = true;
        public bool DryRun;
        public string OutputPath;
    }

    public sealed class PatchResult
    {
        public bool Success;
        public bool AlreadyPatched;
        public bool AlreadyOriginal;
        public bool DryRun;
        public string TargetPath;
        public string OutputPath;
        public string BackupPath;
        public string OriginalSha256;
        public string PatchedSha256;
        public IList<string> RestoredMethods = new List<string>();
        public TimeSpan Elapsed;
        public string Message;

        public PatchResult CloneForState()
        {
            return new PatchResult
            {
                Success = Success,
                AlreadyPatched = AlreadyPatched,
                AlreadyOriginal = AlreadyOriginal,
                DryRun = DryRun,
                TargetPath = TargetPath,
                OutputPath = OutputPath,
                BackupPath = BackupPath,
                OriginalSha256 = OriginalSha256,
                PatchedSha256 = PatchedSha256,
                RestoredMethods = RestoredMethods,
                Elapsed = Elapsed,
                Message = Message
            };
        }
    }

    public sealed class Patcher
    {
        private static readonly Dictionary<string, string> KnownBuilds =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386", "PCL 2.99.0.0 正式版 (413)" },
                { "5b64d6d540c376390470cea3ab99d47661e8bca81be2ba647ea83e7d2e9428fa", "PCL 2.99.0.0 快照版 (414)" },
                { "5582952ded3b443503f66c23b810802bd20f1a3e847b54d9e09653a825859a36", "PCL 2.14.0.0 正式版 (412，限制前实现)" },
                { "d4fe05b8c713c573fcd46e0efa1dc9b5463f5ec4fd03c3d9279d523e48f8d0bd", "PCL 2.99.0.0 正式版 + 官方下载补丁 (413)" }
            };

        private static readonly HashSet<string> AllowedAddedMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Void PCL.PageDownloadInstall::_Lambda$__R3-3(PCL.ModLoader/LoaderTask`2<System.String,PCL.ModDownload/DlClientListResult>)"
        };

        private readonly Action<LogEntry> _log;

        public Patcher(Action<LogEntry> log)
        {
            _log = log;
        }

        private void Emit(LogLevel level, string message)
        {
            Log.Write(_log, level, message);
        }

        public PclTargetInfo Inspect(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new PatchException("请选择 PCL 程序文件（Plain Craft Launcher 2.exe）。");

            var full = Path.GetFullPath(path.Trim().Trim('"'));
            if (Directory.Exists(full))
                full = Path.Combine(full, "Plain Craft Launcher 2.exe");
            if (!File.Exists(full))
                throw new PatchException("找不到文件：" + full);
            if (Directory.Exists(full))
                throw new PatchException("请选择文件，而不是文件夹：" + full);

            var info = new PclTargetInfo
            {
                Path = full,
                Size = new FileInfo(full).Length,
                Sha256 = HashUtil.Sha256File(full),
                BackupPath = full + ".pcl-original.bak"
            };

            try
            {
                var version = FileVersionInfo.GetVersionInfo(full);
                info.FileVersion = version.FileVersion ?? "";
            }
            catch
            {
                info.FileVersion = "";
            }

            using (var target = PclTarget.Load(full))
            {
                info.AssemblyVersion = target.AssemblyVersion;
                string detail;
                info.State = target.GetState(out detail);
                info.Message = detail;
                info.IsSupported = info.State != PatchState.Unsupported;
            }

            info.BuildLabel = DescribeBuild(info);
            info.NoFixNeeded = string.Equals(info.Sha256,
                "5582952ded3b443503f66c23b810802bd20f1a3e847b54d9e09653a825859a36",
                StringComparison.OrdinalIgnoreCase);
            InspectBackup(info);
            return info;
        }

        public PatchResult Apply(string path, PatchOptions options)
        {
            if (options == null) options = new PatchOptions();
            var stopwatch = Stopwatch.StartNew();
            var result = new PatchResult();

            Emit(LogLevel.Step, "[1/7] 检查目标文件...");
            var info = Inspect(path);
            result.TargetPath = info.Path;
            result.OriginalSha256 = info.Sha256;

            Emit(LogLevel.Info, "版本：" + (string.IsNullOrEmpty(info.FileVersion) ? info.AssemblyVersion : info.FileVersion) + " / " + info.BuildLabel);
            Emit(LogLevel.Info, "文件大小：" + HashUtil.FormatSize(info.Size));
            Emit(LogLevel.Info, "SHA-256：" + info.Sha256);
            Emit(LogLevel.Info, "补丁状态：" + info.StateText + "（" + info.Message + "）");

            if (info.State == PatchState.Patched)
            {
                var message = info.NoFixNeeded
                    ? "该版本是下载限制前的官方实现，已经具备完整下载功能，无需修复。"
                    : "该文件已经应用过下载修复，无需重复操作。";
                Emit(LogLevel.Success, message);
                result.Success = true;
                result.AlreadyPatched = true;
                result.Message = message;
                result.OutputPath = info.Path;
                result.PatchedSha256 = info.Sha256;
                result.Elapsed = stopwatch.Elapsed;
                return result;
            }
            if (!info.IsSupported || info.State == PatchState.Unsupported)
                throw new PatchException("此版本不受支持：" + info.Message);
            if (info.State == PatchState.Error)
                throw new PatchException("无法读取此文件：" + info.Message);

            PclProcess.EnsureNotRunning(info.Path);

            var output = string.IsNullOrWhiteSpace(options.OutputPath) || IsSamePath(options.OutputPath, info.Path)
                ? info.Path
                : Path.GetFullPath(options.OutputPath);
            var inPlace = IsSamePath(output, info.Path);
            if (!inPlace) Directory.CreateDirectory(Path.GetDirectoryName(output));

            using (var temp = TempDirectory.Create("patch"))
            {
                string tempFile = null;
                Dictionary<string, string> patchedSemantics = null;
                try
                {
                    using (var target = PclTarget.Load(info.Path))
                    {
                        Emit(LogLevel.Step, "[2/7] 提取 PCL 内嵌依赖...");
                        var dependencies = target.ExtractDependencies(temp.Path);
                        target.AddSearchDirectory(Path.GetDirectoryName(info.Path));
                        Emit(LogLevel.Detail, "依赖目录：" + dependencies);

                        Emit(LogLevel.Step, "[3/7] 分析原始程序集...");
                        var before = MethodSemantics.ComputeAll(target.Assembly);
                        Emit(LogLevel.Detail, "已分析方法：" + before.Count);

                        Emit(LogLevel.Step, "[4/7] 载入恢复模板并恢复五个下载方法...");
                        using (var donor = OpenDonorStream())
                        using (var restorer = new DonorRestorer(target, donor))
                        {
                            result.RestoredMethods = restorer.Restore(_log).ToList();
                        }

                        var after = MethodSemantics.ComputeAll(target.Assembly);
                        patchedSemantics = after;
                        var diff = MethodSemantics.Diff(before, after);
                        ValidateDiff(target, diff);

                        Emit(LogLevel.Step, "[5/7] 校验修复状态...");
                        ValidatePatched(target);

                        var tempName = "." + Path.GetFileName(output) + "." + Guid.NewGuid().ToString("N") + ".pclfix.tmp";
                        tempFile = Path.Combine(Path.GetDirectoryName(output), tempName);

                        Emit(LogLevel.Step, "[6/7] 写入临时文件...");
                        target.Assembly.Write(tempFile, new WriterParameters { WriteSymbols = false });
                        Emit(LogLevel.Detail, "临时文件：" + tempFile + "（" + HashUtil.FormatSize(new FileInfo(tempFile).Length) + "）");
                    }

                    Emit(LogLevel.Step, "[7/7] 校验写入结果...");
                    using (var verified = PclTarget.Load(tempFile))
                    {
                        string detail;
                        var state = verified.GetState(out detail);
                        if (state != PatchState.Patched)
                            throw new PatchException("写入后的程序集校验失败：" + detail);
                        ValidatePatched(verified);

                        if (options.Verify)
                        {
                            // The rewritten file must contain exactly the same IL as the in-memory result.
                            var verifiedSemantics = MethodSemantics.ComputeAll(verified.Assembly);
                            var writeDiff = MethodSemantics.Diff(patchedSemantics, verifiedSemantics);
                            if (!writeDiff.IsEmpty)
                                throw new PatchException("写入后的 IL 与内存中的修复结果不一致，已中止操作。");
                        }
                    }

                    result.PatchedSha256 = HashUtil.Sha256File(tempFile);
                    if (options.DryRun)
                    {
                        Emit(LogLevel.Warn, "演练模式：所有检查均已通过，但没有修改任何文件。");
                        result.Success = true;
                        result.DryRun = true;
                        result.OutputPath = output;
                        result.Elapsed = stopwatch.Elapsed;
                        return result;
                    }

                    if (inPlace && options.CreateBackup)
                        EnsureBackup(info.Path, info.Sha256);

                    PclProcess.EnsureNotRunning(info.Path);
                    var currentHash = HashUtil.Sha256File(info.Path);
                    if (!string.Equals(currentHash, info.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new PatchException("目标文件在修复过程中发生了变化，已中止操作，没有替换文件。");

                    Emit(LogLevel.Step, inPlace ? "应用修复（原子替换）..." : "写入输出文件...");
                    ReplaceFile(tempFile, output);
                    tempFile = null;

                    result.Success = true;
                    result.OutputPath = output;
                    result.BackupPath = inPlace && options.CreateBackup ? info.BackupPath : null;
                    result.PatchedSha256 = HashUtil.Sha256File(output);
                    result.Elapsed = stopwatch.Elapsed;
                    result.Message = inPlace ? "修复完成。" : "已生成修复后的副本。";

                    Emit(LogLevel.Success, result.Message);
                    Emit(LogLevel.Info, "输出文件：" + output);
                    Emit(LogLevel.Info, "输出 SHA-256：" + result.PatchedSha256);
                    if (result.BackupPath != null)
                        Emit(LogLevel.Info, "原版备份：" + result.BackupPath);
                    Emit(LogLevel.Info, "用时：" + result.Elapsed.TotalSeconds.ToString("0.00") + " 秒");
                    return result;
                }
                finally
                {
                    if (tempFile != null && File.Exists(tempFile))
                    {
                        try { File.Delete(tempFile); } catch { }
                    }
                }
            }
        }

        public PatchResult Restore(string path, bool dryRun)
        {
            var stopwatch = Stopwatch.StartNew();
            Emit(LogLevel.Step, "检查目标文件与备份...");
            var info = Inspect(path);
            Emit(LogLevel.Info, "当前状态：" + info.StateText);
            Emit(LogLevel.Info, "备份位置：" + info.BackupPath);

            if (info.State == PatchState.Original)
            {
                Emit(LogLevel.Success, "当前已经是原版，无需还原。");
                return new PatchResult
                {
                    Success = true,
                    AlreadyOriginal = true,
                    TargetPath = info.Path,
                    OutputPath = info.Path,
                    OriginalSha256 = info.Sha256,
                    PatchedSha256 = info.Sha256,
                    Elapsed = stopwatch.Elapsed
                };
            }
            if (info.State != PatchState.Patched && info.State != PatchState.Partial)
                throw new PatchException("此文件不是本工具支持的可还原状态：" + info.StateText);

            if (!info.BackupExists)
                throw new PatchException("找不到原版备份：" + info.BackupPath + "\r\n请使用修复前创建备份的同一文件，或重新获取原版 PCL。");
            if (!info.BackupIsValidOriginal)
                throw new PatchException("备份文件不是有效的原版 PCL，无法安全还原：" + info.BackupPath);
            if (PclProcess.IsRunning(info.Path))
                throw new PatchException("PCL 正在运行，请先完全关闭启动器后再还原。");

            if (dryRun)
            {
                Emit(LogLevel.Warn, "演练模式：备份有效，但没有修改任何文件。");
                return new PatchResult
                {
                    Success = true,
                    DryRun = true,
                    TargetPath = info.Path,
                    OutputPath = info.Path,
                    BackupPath = info.BackupPath,
                    OriginalSha256 = info.Sha256,
                    PatchedSha256 = info.Sha256,
                    Elapsed = stopwatch.Elapsed
                };
            }

            PclProcess.EnsureNotRunning(info.Path);
            var tempFile = Path.Combine(Path.GetDirectoryName(info.Path),
                "." + Path.GetFileName(info.Path) + "." + Guid.NewGuid().ToString("N") + ".restore.tmp");
            try
            {
                File.Copy(info.BackupPath, tempFile, true);
                var tempHash = HashUtil.Sha256File(tempFile);
                if (!string.Equals(tempHash, info.BackupSha256, StringComparison.OrdinalIgnoreCase))
                    throw new PatchException("复制备份时校验失败，未替换文件。");

                Emit(LogLevel.Step, "还原原版（原子替换）...");
                ReplaceFile(tempFile, info.Path);
                tempFile = null;

                var finalHash = HashUtil.Sha256File(info.Path);
                Emit(LogLevel.Success, "已还原为原版 PCL。");
                Emit(LogLevel.Info, "SHA-256：" + finalHash);
                Emit(LogLevel.Info, "用时：" + stopwatch.Elapsed.TotalSeconds.ToString("0.00") + " 秒");
                return new PatchResult
                {
                    Success = true,
                    TargetPath = info.Path,
                    OutputPath = info.Path,
                    BackupPath = info.BackupPath,
                    OriginalSha256 = info.BackupSha256,
                    PatchedSha256 = finalHash,
                    Elapsed = stopwatch.Elapsed,
                    Message = "已还原为原版。"
                };
            }
            finally
            {
                if (tempFile != null && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        private void ValidateDiff(PclTarget target, SemanticDiff diff)
        {
            var expected = new HashSet<string>(RestoreSpecs.All.Select(spec => target.FindMethod(spec).FullName), StringComparer.Ordinal);
            var unexpected = diff.Changed.Where(key => !expected.Contains(key)).ToList();
            if (unexpected.Count > 0)
                throw new PatchException("修复过程中出现了预期之外的方法变化，已中止：\r\n" + string.Join("\r\n", unexpected.Take(10)));

            var unexpectedAdded = diff.Added.Where(key => !AllowedAddedMethods.Contains(key)).ToList();
            if (unexpectedAdded.Count > 0)
                throw new PatchException("修复过程中新增了预期之外的方法，已中止：\r\n" + string.Join("\r\n", unexpectedAdded.Take(10)));
            if (diff.Removed.Count > 0)
                throw new PatchException("修复过程中丢失了方法，已中止：\r\n" + string.Join("\r\n", diff.Removed.Take(10)));
            if (diff.Changed.Count == 0)
                throw new PatchException("没有检测到需要恢复的方法，目标文件可能不是受限版本。");

            Emit(LogLevel.Success, "完整性检查通过：仅 " + diff.Changed.Count + " 个下载相关方法发生变化" +
                (diff.Added.Count > 0 ? "，补充 " + diff.Added.Count + " 个回调方法" : "") + "。");
        }

        private void ValidatePatched(PclTarget target)
        {
            string detail;
            var state = target.GetState(out detail);
            if (state != PatchState.Patched)
                throw new PatchException("修复后的状态校验失败：" + detail);

            var loader = target.FindMethod(RestoreSpecs.All[2]);
            var modpack = target.FindMethod(RestoreSpecs.All[0]);
            var resourceDetail = target.FindMethod(RestoreSpecs.All[4]);

            if (!PclTarget.ReferencesField(loader, "DlClientListLoader"))
                throw new PatchException("修复后的下载页没有指向正常的版本列表加载器。");
            if (!PclTarget.ReferencesMethod(modpack, "SelectFile"))
                throw new PatchException("修复后的整合包安装入口没有恢复文件选择对话框。");
            if (!PclTarget.ReferencesMethod(resourceDetail, "Install_Click") &&
                !PclTarget.ReferencesMethod(resourceDetail, "_Lambda$__R18-5"))
                throw new PatchException("修复后的资源详情页没有恢复安装按钮。");

            foreach (var spec in RestoreSpecs.All)
            {
                if (PclTarget.HasGuard(target.FindMethod(spec)))
                    throw new PatchException("修复后仍检测到下载限制代码：" + spec);
            }
            Emit(LogLevel.Success, "五项下载方法均已恢复，限制代码已清除。");
        }

        private void EnsureBackup(string targetPath, string expectedHash)
        {
            var backup = targetPath + ".pcl-original.bak";
            if (File.Exists(backup))
            {
                var backupHash = HashUtil.Sha256File(backup);
                if (!string.Equals(backupHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new PatchException("备份文件已存在，但与当前目标文件不一致，未覆盖任何文件：\r\n" + backup);
                Emit(LogLevel.Info, "原版备份已存在：" + backup);
                return;
            }

            Emit(LogLevel.Step, "创建原版备份...");
            var tempBackup = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(targetPath, tempBackup, true);
                var hash = HashUtil.Sha256File(tempBackup);
                if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new PatchException("创建备份时校验失败，已中止操作。");
                File.Move(tempBackup, backup);
                Emit(LogLevel.Success, "原版备份：" + backup);
            }
            finally
            {
                if (File.Exists(tempBackup))
                {
                    try { File.Delete(tempBackup); } catch { }
                }
            }
        }

        private static void ReplaceFile(string source, string destination)
        {
            if (File.Exists(destination))
            {
                var attributes = File.GetAttributes(destination);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(destination, attributes & ~FileAttributes.ReadOnly);

                try
                {
                    File.Replace(source, destination, null, true);
                    return;
                }
                catch (IOException)
                {
                    // Fall back to copy + delete below.
                }
                catch (PlatformNotSupportedException)
                {
                }
            }

            File.Copy(source, destination, true);
            File.Delete(source);
        }

        private static Stream OpenDonorStream()
        {
            return EmbeddedResources.OpenDonor();
        }

        private static string DescribeBuild(PclTargetInfo info)
        {
            string label;
            if (KnownBuilds.TryGetValue(info.Sha256, out label)) return label;
            if (info.State == PatchState.Patched) return "PCL 2.99.0.0（第三方修复版本）";
            return "PCL 2.99.0.0（未知快照构建）";
        }

        private static void InspectBackup(PclTargetInfo info)
        {
            if (!File.Exists(info.BackupPath)) return;
            info.BackupExists = true;
            try
            {
                info.BackupSha256 = HashUtil.Sha256File(info.BackupPath);
                using (var backup = PclTarget.Load(info.BackupPath))
                {
                    string detail;
                    info.BackupIsValidOriginal = backup.GetState(out detail) == PatchState.Original;
                }
            }
            catch
            {
                info.BackupIsValidOriginal = false;
            }
        }

        private static bool IsSamePath(string left, string right)
        {
            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}

