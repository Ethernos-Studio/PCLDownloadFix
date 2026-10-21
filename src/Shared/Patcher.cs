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
        private const string PreRestrictionHash = "5582952ded3b443503f66c23b810802bd20f1a3e847b54d9e09653a825859a36";
        private static readonly TimeSpan WriteLockTimeout = TimeSpan.FromSeconds(10);

        private static readonly Dictionary<string, string> KnownBuilds =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386", "PCL 2.99.0.0 正式版 (413)" },
                { "5b64d6d540c376390470cea3ab99d47661e8bca81be2ba647ea83e7d2e9428fa", "PCL 2.99.0.0 快照版 (414)" },
                { PreRestrictionHash, "PCL 2.14.0.0 正式版 (412，限制前实现)" },
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
                BackupPath = BackupStore.LegacyPath(full)
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

                var build = target.GetBuildInfo();
                info.VersionCode = build.VersionCode;
                info.BuildType = build.BuildType;
                info.BuildKey = build.BuildKey;
                info.VersionText = string.IsNullOrEmpty(build.VersionDisplay)
                    ? build.VersionBaseName
                    : build.VersionDisplay;
            }

            ApplyKnownBuildVersion(info);

            if (info.State == PatchState.Unsupported)
            {
                // A known pre-restriction build can still be identified by hash even if
                // its method shapes differ from the five known PCL 2.99 signatures.
                if (string.Equals(info.Sha256, PreRestrictionHash, StringComparison.OrdinalIgnoreCase))
                {
                    info.State = PatchState.Unrestricted;
                    info.Message = "该版本是下载限制前的官方实现，已具备完整下载功能。";
                }
            }

            InspectBackup(info);

            // GetState cannot tell "patched by this tool" from "officially unrestricted"
            // because both look the same in IL. A valid original backup is the evidence
            // that this file was patched and can be restored.
            if (info.State == PatchState.Unrestricted && info.BackupExists && info.BackupIsValidOriginal)
                info.State = PatchState.Patched;

            if (string.Equals(info.Sha256, PreRestrictionHash, StringComparison.OrdinalIgnoreCase))
                info.State = PatchState.Unrestricted;

            info.IsPreRestriction = info.VersionCode.HasValue
                ? info.VersionCode.Value < 413
                : string.Equals(info.Sha256, PreRestrictionHash, StringComparison.OrdinalIgnoreCase) ||
                  (!string.IsNullOrEmpty(info.AssemblyVersion) &&
                   info.AssemblyVersion.StartsWith("2.14", StringComparison.Ordinal));

            var autoUpdate = AutoUpdateSettings.Read(full);
            info.SetupIniPath = autoUpdate.SetupIniPath;
            info.AutoUpdateState = autoUpdate.State;
            info.AutoUpdateValue = autoUpdate.Value;
            info.AutoUpdateText = autoUpdate.Text;
            info.AutoUpdateError = autoUpdate.Error;

            info.NoFixNeeded = info.State == PatchState.Unrestricted;
            info.IsSupported = info.State != PatchState.Unsupported && info.State != PatchState.Error;
            info.BuildLabel = DescribeBuild(info);
            return info;
        }

        public AutoUpdateChange DisableAutoUpdate(string path)
        {
            var info = Inspect(path);
            return AutoUpdateSettings.Disable(info.Path, true, _log);
        }

        public AutoUpdateChange EnableAutoUpdate(string path)
        {
            var info = Inspect(path);
            return AutoUpdateSettings.Enable(info.Path, _log);
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

            if (info.State == PatchState.Patched || info.State == PatchState.Unrestricted)
            {
                var message = info.NoFixNeeded
                    ? "该文件已具备完整下载功能，无需修复。"
                    : "该文件已经应用过下载修复，无需重复操作。";
                Emit(LogLevel.Success, message);
                result.Success = true;
                result.AlreadyPatched = true;
                result.Message = message;
                result.OutputPath = info.Path;

                // Keep --output meaningful even when the input is already usable: the
                // caller asked for a copy, not for another patch operation.
                if (!string.IsNullOrWhiteSpace(options.OutputPath) && !IsSamePath(options.OutputPath, info.Path))
                {
                    var copy = Path.GetFullPath(options.OutputPath);
                    if (!options.DryRun)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(copy));
                        File.Copy(info.Path, copy, true);
                    }
                    result.OutputPath = copy;
                }

                result.PatchedSha256 = info.Sha256;
                result.Elapsed = stopwatch.Elapsed;
                return result;
            }
            if (!info.IsSupported || info.State == PatchState.Unsupported)
                throw new UnsupportedTargetException("此版本不受支持：" + info.Message);
            if (info.State == PatchState.Error)
                throw new PatchException("无法读取此文件：" + info.Message);

            var output = string.IsNullOrWhiteSpace(options.OutputPath) || IsSamePath(options.OutputPath, info.Path)
                ? info.Path
                : Path.GetFullPath(options.OutputPath);
            var inPlace = IsSamePath(output, info.Path);
            if (!inPlace) Directory.CreateDirectory(Path.GetDirectoryName(output));

            if (inPlace && info.State == PatchState.Partial &&
                (!info.BackupExists || !info.BackupIsValidOriginal) &&
                options.CreateBackup)
            {
                throw new PatchException(
                    "检测到部分修复，但没有可用的原版备份。\r\n" +
                    "继续修复会把部分修复后的文件误当作原版备份，导致以后无法安全还原，因此已中止。\r\n" +
                    "请重新获取官方原版 PCL 后再执行修复。");
            }

            PclProcess.EnsureNotRunning(info.Path);

            using (var temp = TempDirectory.Create("patch"))
            {
                string tempFile = null;
                Dictionary<string, string> patchedSemantics = null;
                AssemblyFingerprint originalFingerprint = null;
                try
                {
                    using (var target = PclTarget.Load(info.Path))
                    {
                        originalFingerprint = AssemblyFingerprint.Capture(target.Assembly);

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

                        var patchedFingerprint = AssemblyFingerprint.Capture(target.Assembly);
                        originalFingerprint.EnsureSame(patchedFingerprint, "修复过程中");

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
                        if (state != PatchState.Patched && state != PatchState.Unrestricted)
                            throw new PatchException("写入后的程序集校验失败：" + detail);
                        ValidatePatched(verified);
                        originalFingerprint.EnsureSame(AssemblyFingerprint.Capture(verified.Assembly), "写入后");

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

                    using (TargetLock.Acquire(info.Path, WriteLockTimeout))
                    {
                        if (inPlace && options.CreateBackup)
                        {
                            if (info.State == PatchState.Partial && info.BackupExists && info.BackupIsValidOriginal)
                            {
                                // Preserve the original backup that already belongs to this
                                // partially patched file; never archive it as if it were the
                                // previous version's backup.
                                result.BackupPath = info.BackupPath;
                                Emit(LogLevel.Info, "保留现有原版备份：" + result.BackupPath);
                            }
                            else
                            {
                                result.BackupPath = BackupStore.EnsureOriginalBackup(info.Path, info.Sha256, _log);
                            }
                        }

                        PclProcess.EnsureNotRunning(info.Path);
                        var currentHash = HashUtil.Sha256File(info.Path);
                        if (!string.Equals(currentHash, info.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new PatchException("目标文件在修复过程中发生了变化，已中止操作，没有替换文件。");

                        Emit(LogLevel.Step, inPlace ? "应用修复（原子替换）..." : "写入输出文件...");
                        FileOps.Replace(tempFile, output);
                        tempFile = null;
                    }

                    var finalHash = HashUtil.Sha256File(output);
                    if (!string.Equals(finalHash, result.PatchedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new PatchException("替换后的文件哈希与校验结果不一致，请立即检查：" + output +
                            (result.BackupPath == null ? "" : "\r\n原版备份：" + result.BackupPath));
                    }

                    result.Success = true;
                    result.OutputPath = output;
                    result.PatchedSha256 = finalHash;
                    result.Elapsed = stopwatch.Elapsed;
                    result.Message = inPlace ? "修复完成。" : "已生成修复后的副本。";

                    if (inPlace)
                    {
                        var originalHash = info.State == PatchState.Partial
                            ? info.BackupSha256
                            : info.Sha256;
                        var manifest = new PatchManifest
                        {
                            OriginalSha256 = originalHash,
                            PatchedSha256 = result.PatchedSha256,
                            BackupFile = options.CreateBackup && result.BackupPath != null
                                ? Path.GetFileName(result.BackupPath)
                                : "",
                            Build = info.BuildLabel,
                            ToolVersion = DisclaimerText.ProductVersion,
                            CreatedUtc = DateTime.UtcNow.ToString("o")
                        };
                        if (!BackupStore.TryWriteManifest(info.Path, manifest))
                        {
                            // Never leave a stale manifest that points at a previous build's
                            // backup; the backup itself is still usable via the legacy path.
                            BackupStore.DeleteManifest(info.Path);
                            Emit(LogLevel.Warn, "无法写入还原清单（.pclfix.json），但文件修复结果不受影响。");
                        }
                    }

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

            if (info.State == PatchState.Original || info.State == PatchState.Unrestricted)
            {
                var message = info.State == PatchState.Unrestricted
                    ? "当前文件已具备完整下载功能，无需还原。"
                    : "当前已经是原版，无需还原。";
                Emit(LogLevel.Success, message);
                return new PatchResult
                {
                    Success = true,
                    AlreadyOriginal = true,
                    TargetPath = info.Path,
                    OutputPath = info.Path,
                    OriginalSha256 = info.Sha256,
                    PatchedSha256 = info.Sha256,
                    Message = message,
                    Elapsed = stopwatch.Elapsed
                };
            }
            if (info.State != PatchState.Patched && info.State != PatchState.Partial)
                throw new UnsupportedTargetException("此文件不是本工具支持的可还原状态：" + info.StateText);

            if (!info.BackupExists)
                throw new PatchException("找不到原版备份：" + info.BackupPath + "\r\n请使用修复前创建备份的同一文件，或重新获取原版 PCL。");
            if (!info.BackupIsValidOriginal)
                throw new PatchException("备份文件不是有效的原版 PCL，无法安全还原：" + info.BackupPath);
            if (!string.IsNullOrEmpty(info.BackupAssemblyVersion) &&
                !string.IsNullOrEmpty(info.AssemblyVersion) &&
                !string.Equals(info.BackupAssemblyVersion, info.AssemblyVersion, StringComparison.Ordinal))
            {
                throw new PatchException("备份版本（" + info.BackupAssemblyVersion + "）与目标版本（" +
                    info.AssemblyVersion + "）不一致，拒绝还原。\r\n备份：" + info.BackupPath);
            }
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

            using (TargetLock.Acquire(info.Path, WriteLockTimeout))
            {
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
                    FileOps.Replace(tempFile, info.Path);
                    tempFile = null;
                }
                finally
                {
                    if (tempFile != null && File.Exists(tempFile))
                    {
                        try { File.Delete(tempFile); } catch { }
                    }
                }
            }

            var finalHash = HashUtil.Sha256File(info.Path);
            if (!string.Equals(finalHash, info.BackupSha256, StringComparison.OrdinalIgnoreCase))
                throw new PatchException("还原后文件哈希与备份不一致，请立即检查：" + info.Path);

            using (var restored = PclTarget.Load(info.Path))
            {
                string detail;
                var state = restored.GetState(out detail);
                if (state != PatchState.Original && state != PatchState.Unrestricted)
                    throw new PatchException("还原后的文件未通过原版校验：" + detail);
            }

            BackupStore.DeleteManifest(info.Path);
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
            if (state != PatchState.Patched && state != PatchState.Unrestricted)
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

        private static Stream OpenDonorStream()
        {
            return EmbeddedResources.OpenDonor();
        }

        private static void ApplyKnownBuildVersion(PclTargetInfo info)
        {
            if (info.VersionCode.HasValue && info.BuildType.HasValue) return;

            int versionCode;
            int buildType;
            switch ((info.Sha256 ?? "").ToLowerInvariant())
            {
                case PreRestrictionHash:
                    versionCode = 411;
                    buildType = 50;
                    break;
                case "6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386":
                    versionCode = 413;
                    buildType = 50;
                    break;
                case "5b64d6d540c376390470cea3ab99d47661e8bca81be2ba647ea83e7d2e9428fa":
                    versionCode = 414;
                    buildType = 0;
                    break;
                default:
                    return;
            }

            info.VersionCode = versionCode;
            info.BuildType = buildType;
            info.BuildKey = PclBuildInfo.FormatBuildKey(versionCode, buildType);
        }

        private static string DescribeBuild(PclTargetInfo info)
        {
            string label;
            if (KnownBuilds.TryGetValue(info.Sha256, out label)) return label;

            var version = string.IsNullOrEmpty(info.AssemblyVersion) ? info.FileVersion : info.AssemblyVersion;
            if (string.IsNullOrEmpty(version)) version = "未知版本";

            if (info.State == PatchState.Patched)
                return "PCL " + version + "（第三方修复版本）";
            if (info.State == PatchState.Unrestricted)
                return "PCL " + version + "（完整版本，无需修复）";
            return "PCL " + version + "（未识别构建）";
        }

        private static void InspectBackup(PclTargetInfo info)
        {
            PatchManifest manifest;
            var path = BackupStore.ResolveBackupPath(info.Path, info.Sha256, out manifest);
            info.BackupPath = path ?? BackupStore.LegacyPath(info.Path);
            if (path == null) return;

            info.BackupExists = true;
            if (manifest != null && !string.IsNullOrEmpty(manifest.BackupFile))
            {
                var manifestPath = Path.Combine(Path.GetDirectoryName(info.Path) ?? "",
                    Path.GetFileName(manifest.BackupFile));
                info.BackupFromManifest = string.Equals(Path.GetFullPath(path), Path.GetFullPath(manifestPath),
                    StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                info.BackupSha256 = HashUtil.Sha256File(path);
                using (var backup = PclTarget.Load(path))
                {
                    string detail;
                    var state = backup.GetState(out detail);
                    info.BackupIsValidOriginal = state == PatchState.Original || state == PatchState.Unrestricted;
                    info.BackupAssemblyVersion = backup.AssemblyVersion;
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
