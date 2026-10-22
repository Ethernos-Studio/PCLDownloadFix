using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PCLDownloadFix.Cli
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Bootstrap.Init();
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch
            {
            }
            return Cli.Run(args);
        }
    }

    internal static class Cli
    {
        private const string Product = "PCL 下载修复器（第三方）";
        private const string Author = "Ethernos Studio";
        private const int SchemaVersion = 1;
        private static readonly object ConsoleLock = new object();

        private static TextWriter _stdout = Console.Out;
        private static bool _json;
        private static string _command = "";

        public static int Run(string[] args)
        {
            _stdout = Console.Out;
            _json = args != null && args.Any(IsJsonFlag);
            if (_json)
            {
                // Keep stdout reserved for exactly one JSON document. Every human-facing
                // line (banner, progress, disclaimer) is still available on stderr.
                Console.SetOut(Console.Error);
            }

            Console.Title = Product + " · 控制台版 v" + DisclaimerText.ProductVersion + " · " + Author;
            if (args == null || args.Length == 0)
            {
                PrintBanner();
                PrintHelp();
                WaitIfInteractive();
                return 0;
            }

            _command = args[0].Trim().ToLowerInvariant();
            try
            {
                switch (_command)
                {
                    case "help":
                    case "-h":
                    case "--help":
                    case "/?":
                        PrintBanner();
                        PrintHelp();
                        return 0;
                    case "version":
                    case "--version":
                        Console.WriteLine(Product + " v" + DisclaimerText.ProductVersion + " · " + Author);
                        return 0;
                    case "gui":
                        // The GUI shows the full disclaimer before its main window.
                        return StartGui();
                    case "selfcheck":
                    case "self-check":
                    case "integrity":
                        PrintBanner();
                        return SelfCheckCommand(args);
                }

                if (_command == "patch" || _command == "apply" || _command == "fix")
                {
                    PrintBanner();
                    if (!EnsureDisclaimer(args)) return 3;
                    return Patch(args);
                }
                if (_command == "restore" || _command == "revert")
                {
                    PrintBanner();
                    if (!EnsureDisclaimer(args)) return 3;
                    return Restore(args);
                }
                if (_command == "disable-update" || _command == "update-off")
                {
                    PrintBanner();
                    if (!EnsureDisclaimer(args)) return 3;
                    return ChangeAutoUpdate(args, true);
                }
                if (_command == "enable-update" || _command == "update-on")
                {
                    PrintBanner();
                    if (!EnsureDisclaimer(args)) return 3;
                    return ChangeAutoUpdate(args, false);
                }
                if (_command == "verify" || _command == "check" || _command == "info")
                {
                    PrintBanner();
                    if (!EnsureDisclaimer(args)) return 3;
                    return Verify(args, _command == "info");
                }
                if (_command == "hmcl-info" || _command == "hmcl-patch" || _command == "hmcl-restore")
                {
                    PrintBanner();
                    if (!EnsureDisclaimer(args)) return 3;
                    return HmclCommand(args, _command);
                }
                if (_command == "fcl-patch")
                {
                    PrintBanner();
                    if (!EnsureDisclaimer(args)) return 3;
                    return FclCommand(args);
                }

                if (File.Exists(args[0]))
                {
                    PrintBanner();
                    if (!EnsureDisclaimer(args)) return 3;
                    return Patch(new[] { "patch", args[0] }.Concat(args.Skip(1)).ToArray());
                }

                PrintBanner();
                return Fail("未知命令：" + args[0], 1);
            }
            catch (UnsupportedTargetException ex)
            {
                return Fail(ex.Message, 2);
            }
            catch (PatchException ex)
            {
                return Fail(ex.Message, 1);
            }
            catch (Exception ex)
            {
                return Fail("发生未预期的错误：" + ex.Message, 1);
            }
            finally
            {
                try { Console.Out.Flush(); } catch { }
                try { _stdout.Flush(); } catch { }
            }
        }

        private static int Fail(string message, int exitCode)
        {
            if (_json)
                WriteJson(ErrorJson(_command, message, exitCode));
            else
                Error(message);
            return exitCode;
        }

        private static bool EnsureDisclaimer(string[] args)
        {
            if (_json)
            {
                if (args.Any(IsDisclaimerAccepted)) return true;
                WriteJson(ErrorJson(_command,
                    "JSON 模式为非交互模式，必须追加 --yes 才能确认免责声明并继续。", 3));
                return false;
            }

            if (args.Any(IsDisclaimerAccepted))
            {
                Console.WriteLine("免责声明：已通过 --yes 确认。");
                Console.WriteLine();
                return true;
            }

            PrintDisclaimer();
            if (Console.IsInputRedirected)
            {
                Error("当前为非交互环境，必须追加 --yes 才能确认免责声明并继续。");
                return false;
            }

            Console.Write("如已阅读并同意，请输入 Y 后回车；输入其他内容退出：");
            var answer = Console.ReadLine();
            if (string.Equals(answer == null ? null : answer.Trim(), "y", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(answer == null ? null : answer.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                return true;
            }

            Console.WriteLine();
            Warning("已取消：未确认免责声明。");
            return false;
        }

        private static bool IsDisclaimerAccepted(string argument)
        {
            return string.Equals(argument, "--yes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(argument, "-y", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(argument, "--accept-disclaimer", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsJsonFlag(string argument)
        {
            return string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase);
        }

        private static void PrintDisclaimer()
        {
            Header("免责声明");
            foreach (var paragraph in DisclaimerText.Paragraphs)
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("  " + paragraph);
                Console.WriteLine();
            }
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("  项目主页：" + DisclaimerText.ProjectUrl);
            Console.WriteLine("  参考项目：" + DisclaimerText.ReferenceUrl);
            Console.ResetColor();
            Console.WriteLine();
        }

        private static int SelfCheckCommand(string[] args)
        {
            var result = SelfCheck.Run();
            if (_json)
            {
                WriteJson(SelfCheckJson(result));
                return result.AllEmbeddedOk ? 0 : 1;
            }

            Header("本程序完整性");
            Console.WriteLine("程序路径  : " + result.ExePath);
            Console.WriteLine("文件大小  : " + HashUtil.FormatSize(result.Size));
            Console.WriteLine("EXE SHA-256: " + result.ExeSha256);
            Console.WriteLine("恢复模板  : " + (result.DonorOk ? "通过" : "失败") + " " + result.DonorSha256);
            Console.WriteLine("Mono.Cecil: " + (result.CecilOk ? "通过" : "失败") + " " + result.CecilSha256);
            Console.WriteLine();
            if (result.AllEmbeddedOk)
                Success("内嵌组件校验通过。");
            else
                Error("内嵌组件校验失败，程序可能已损坏或被修改。");
            Console.WriteLine("请将上面的 EXE SHA-256 与 GitHub Release / SHA256SUMS.txt 公布的值比对；一致即表示文件未被修改。");
            Console.WriteLine("项目主页：" + result.ProjectUrl);
            return result.AllEmbeddedOk ? 0 : 1;
        }

        private static int HmclCommand(string[] args, string command)
        {
            var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(path))
                throw new PatchException("请指定 HMCL 的 .jar 或 .exe 路径。\r\n  hmcl-patch \"D:\\HMCL\\HMCL-3.16.5.jar\"");
            path = Path.GetFullPath(path.Trim().Trim('"'));

            if (command == "hmcl-info")
            {
                var info = HmclPatcher.Inspect(path);
                if (_json)
                {
                    WriteJson(HmclJson(info, null, command));
                    return info.Success ? 0 : 2;
                }
                Header("HMCL 信息");
                Console.WriteLine("文件    : " + info.Path);
                Console.WriteLine("类型    : " + (info.Kind == HmclPackageKind.Executable ? "EXE（追加 JAR）" : "JAR"));
                Console.WriteLine("版本    : " + (info.Version ?? "未知"));
                Console.WriteLine("地区限制: " + (info.HasRegionGate ? (info.IsPatched ? "已修复" : "存在") : "不存在"));
                Console.WriteLine("原版备份: " + (info.BackupExists ? info.BackupPath : "无"));
                if (!info.Success) Error(info.Error);
                return info.Success ? 0 : 2;
            }

            if (command == "hmcl-restore")
            {
                var restored = HmclPatcher.Restore(path);
                if (_json)
                {
                    WriteJson(HmclJson(restored.Info, restored, command));
                    return restored.Success ? 0 : 1;
                }
                Header("恢复 HMCL");
                Console.WriteLine(restored.Message);
                return restored.Success ? 0 : 1;
            }

            var backup = !args.Any(a => string.Equals(a, "--no-backup", StringComparison.OrdinalIgnoreCase));
            var result = HmclPatcher.Patch(path, backup);
            if (_json)
            {
                WriteJson(HmclJson(result.Info, result, command));
                return result.Success ? 0 : 1;
            }
            Header("修复 HMCL");
            Console.WriteLine(result.Message);
            if (!string.IsNullOrEmpty(result.BackupPath))
                Console.WriteLine("原版备份: " + result.BackupPath);
            if (!result.Success) Error(result.Info == null ? "修复失败。" : result.Info.Error);
            return result.Success ? 0 : 1;
        }

        private static int FclCommand(string[] args)
        {
            var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(path))
                throw new PatchException("请指定 FCL APK 路径。\r\n  fcl-patch \"D:\\FCL-arm64.apk\"");
            path = Path.GetFullPath(path.Trim().Trim('"'));

            var options = new FclPatchOptions();
            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--tools":
                        if (i + 1 < args.Length) options.ToolsDirectory = args[++i];
                        break;
                    case "--java":
                        if (i + 1 < args.Length) options.JavaPath = args[++i];
                        break;
                    case "--allow-ungated":
                        options.AllowWithoutGate = true;
                        break;
                }
            }
            options.Log = text =>
            {
                if (!_json) Console.WriteLine("  " + text);
            };

            var result = FclPatcher.Patch(path, options);
            if (_json)
            {
                var builder = new StringBuilder();
                builder.AppendLine("{");
                builder.AppendLine("  \"schema\": " + SchemaVersion + ",");
                builder.AppendLine("  \"ok\": " + Bool(result.Success) + ",");
                builder.AppendLine("  \"exitCode\": " + (result.Success ? 0 : 1) + ",");
                builder.AppendLine("  \"command\": \"fcl-patch\",");
                builder.AppendLine("  \"source\": " + JsonStringOrNull(result.SourcePath) + ",");
                builder.AppendLine("  \"output\": " + JsonStringOrNull(result.OutputPath) + ",");
                builder.AppendLine("  \"work\": " + JsonStringOrNull(result.WorkDirectory) + ",");
                builder.AppendLine("  \"log\": " + JsonStringOrNull(result.LogPath) + ",");
                builder.AppendLine("  \"message\": " + JsonStringOrNull(result.Message) + ",");
                builder.AppendLine("  \"steps\": [" +
                    string.Join(", ", result.Steps.Select(s => "\"" + JsonEscape(s) + "\"")) + "]");
                builder.AppendLine("}");
                WriteJson(builder.ToString().TrimEnd());
                return result.Success ? 0 : 1;
            }

            Header("FCL 共存修复");
            foreach (var step in result.Steps) Console.WriteLine("  · " + step);
            if (result.Success)
            {
                Success(result.Message);
                Console.WriteLine("输出：" + result.OutputPath);
                Console.WriteLine("日志：" + result.LogPath);
                Console.WriteLine("注意：这是使用新证书签名的共存版，无法覆盖官方 FCL；请先备份游戏数据。");
                return 0;
            }
            Error(result.Message);
            if (!string.IsNullOrEmpty(result.LogPath)) Console.WriteLine("日志：" + result.LogPath);
            return 1;
        }

        private static string HmclJson(HmclInfo info, HmclPatchResult result, string command)
        {
            var ok = result == null ? info != null && info.Success : result.Success;
            var builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"schema\": " + SchemaVersion + ",");
            builder.AppendLine("  \"ok\": " + Bool(ok) + ",");
            builder.AppendLine("  \"exitCode\": " + (ok ? 0 : 1) + ",");
            builder.AppendLine("  \"command\": \"" + JsonEscape(command) + "\",");
            builder.AppendLine("  \"target\": " + JsonStringOrNull(info == null ? null : info.Path) + ",");
            builder.AppendLine("  \"kind\": " + JsonStringOrNull(info == null ? null :
                (info.Kind == HmclPackageKind.Executable ? "exe" : "jar")) + ",");
            builder.AppendLine("  \"version\": " + JsonStringOrNull(info == null ? null : info.Version) + ",");
            builder.AppendLine("  \"hasRegionGate\": " + Bool(info != null && info.HasRegionGate) + ",");
            builder.AppendLine("  \"patched\": " + Bool(info != null && info.IsPatched) + ",");
            builder.AppendLine("  \"backup\": " + JsonStringOrNull(
                result != null && !string.IsNullOrEmpty(result.BackupPath)
                    ? result.BackupPath
                    : info == null ? null : info.BackupPath) + ",");
            builder.AppendLine("  \"changed\": " + Bool(result != null && result.Changed) + ",");
            builder.AppendLine("  \"message\": " + JsonStringOrNull(result == null ? null : result.Message) + ",");
            builder.AppendLine("  \"error\": " + JsonStringOrNull(info == null ? null : info.Error));
            builder.AppendLine("}");
            return builder.ToString().TrimEnd();
        }

        private static int Patch(string[] args)
        {
            var options = ParseOptions(args, out var path, out var pause, out _);
            if (path == null) path = FindPcl();
            if (path == null)
                throw new PatchException("未找到 PCL。请把本工具放到 PCL 同目录，或执行：\r\n  PCLDownloadFix.Console.exe patch \"D:\\PCL\\Plain Craft Launcher 2.exe\"");

            Header("应用下载修复");
            Console.WriteLine("目标文件：" + Path.GetFullPath(path));
            Console.WriteLine();

            var patcher = new Patcher(PrintLog);
            var patchOptions = new PatchOptions
            {
                CreateBackup = !options.NoBackup,
                Verify = !options.NoVerify,
                DryRun = options.DryRun,
                OutputPath = options.Output
            };

            var result = patcher.Apply(path, patchOptions);
            if (_json)
            {
                WriteJson(PatchJson(result));
                return 0;
            }

            Console.WriteLine();
            if (result.AlreadyPatched)
            {
                Success(string.IsNullOrEmpty(result.Message) ? "此文件已经修复，无需重复操作。" : result.Message);
            }
            else if (result.DryRun)
            {
                Warning("演练完成，未修改任何文件。");
            }
            else
            {
                Success("修复完成。");
                Console.WriteLine("输出文件：" + result.OutputPath);
                Console.WriteLine("输出 SHA-256：" + result.PatchedSha256);
                if (!string.IsNullOrEmpty(result.BackupPath))
                    Console.WriteLine("原版备份：" + result.BackupPath);
            }
            WaitIfRequested(pause);
            return 0;
        }

        private static int Restore(string[] args)
        {
            var options = ParseOptions(args, out var path, out var pause, out _);
            if (path == null) path = FindPcl();
            if (path == null) throw new PatchException("未找到 PCL，请指定要还原的 EXE 路径。");

            Header("还原原版");
            Console.WriteLine("目标文件：" + Path.GetFullPath(path));
            Console.WriteLine();

            var patcher = new Patcher(PrintLog);
            var result = patcher.Restore(path, options.DryRun);
            if (_json)
            {
                WriteJson(RestoreJson(result));
                return 0;
            }

            Console.WriteLine();
            if (result.AlreadyOriginal)
                Success("当前已经是原版，无需还原。");
            else if (result.DryRun)
                Warning("演练完成，未修改任何文件。");
            else
                Success("已还原为原版 PCL。");
            WaitIfRequested(pause);
            return 0;
        }

        private static int ChangeAutoUpdate(string[] args, bool disable)
        {
            var options = ParseOptions(args, out var path, out var pause, out _);
            if (path == null) path = FindPcl();
            if (path == null) throw new PatchException("未找到 PCL，请指定 Plain Craft Launcher 2.exe 的路径。");
            if (options.DryRun)
                throw new PatchException("disable-update/enable-update 不支持 --dry-run；请先关闭 PCL 并确认目标路径。");
            if (!string.IsNullOrEmpty(options.Output))
                throw new PatchException("disable-update/enable-update 不支持 --output。");

            Header(disable ? "关闭 PCL 自动更新" : "恢复 PCL 自动更新");
            Console.WriteLine("目标文件：" + Path.GetFullPath(path));
            Console.WriteLine();

            var patcher = new Patcher(PrintLog);
            var result = disable ? patcher.DisableAutoUpdate(path) : patcher.EnableAutoUpdate(path);

            if (_json)
            {
                WriteJson(AutoUpdateJson(result, disable));
                return 0;
            }

            Console.WriteLine();
            Success(result.Message);
            Console.WriteLine("设置文件：" + result.SetupIniPath);
            if (!string.IsNullOrEmpty(result.CurrentValue))
                Console.WriteLine("SystemSystemUpdate：" + result.CurrentValue);
            if (!string.IsNullOrEmpty(result.BackupPath))
                Console.WriteLine("设置备份：" + result.BackupPath);
            WaitIfRequested(pause);
            return 0;
        }

        private static int Verify(string[] args, bool infoOnly)
        {
            var options = ParseOptions(args, out var path, out var pause, out _);
            if (path == null) path = FindPcl();
            if (path == null) throw new PatchException("未找到 PCL，请指定要检查的 EXE 路径。");

            var patcher = new Patcher(PrintLog);
            var info = patcher.Inspect(path);

            if (_json)
            {
                WriteJson(VerifyJson(info));
            }
            else
            {
                Header(infoOnly ? "文件信息" : "校验结果");
                Console.WriteLine("文件路径  : " + info.Path);
                Console.WriteLine("文件大小  : " + HashUtil.FormatSize(info.Size));
                Console.WriteLine("文件版本  : " + (string.IsNullOrEmpty(info.FileVersion) ? info.AssemblyVersion : info.FileVersion));
                Console.WriteLine("构建识别  : " + info.BuildLabel);
                if (!string.IsNullOrEmpty(info.BuildKey)) Console.WriteLine("构建版本  : " + info.BuildKey);
                Console.WriteLine("补丁状态  : " + info.StateText);
                Console.WriteLine("状态说明  : " + info.Message);
                Console.WriteLine("SHA-256   : " + info.Sha256);
                Console.WriteLine("原版备份  : " + info.BackupText);
                if (info.BackupExists) Console.WriteLine("备份 SHA  : " + info.BackupSha256);
                Console.WriteLine("自动更新  : " + info.AutoUpdateText +
                    (string.IsNullOrEmpty(info.AutoUpdateError) ? "" : "（" + info.AutoUpdateError + "）"));
                if (!string.IsNullOrEmpty(info.SetupIniPath)) Console.WriteLine("设置文件  : " + info.SetupIniPath);
                Console.WriteLine();

                if (info.State == PatchState.Patched)
                    Success(info.NoFixNeeded
                        ? "该版本是下载限制前的官方实现，已具备完整下载功能，无需修复。"
                        : "此文件已应用下载修复。");
                else if (info.State == PatchState.Unrestricted)
                    Success("该文件已具备完整下载功能，无需修复。");
                else if (info.State == PatchState.Original)
                    Warning("此文件是受限原版，可执行 patch 命令应用修复。");
                else if (info.State == PatchState.Partial)
                    Warning("此文件只有部分修复，可重新执行 patch 进行完整修复。");
                else
                    Error("不支持或无法识别此文件。");
            }

            WaitIfRequested(pause);
            return info.State == PatchState.Unsupported || info.State == PatchState.Error ? 2 : 0;
        }

        private static int StartGui()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "PCL下载修复器.exe"),
                Path.Combine(baseDir, "PCLDownloadFix.Gui.exe")
            };
            var exe = candidates.FirstOrDefault(File.Exists);
            if (exe == null)
                throw new PatchException("找不到 GUI 程序，请确认 PCL下载修复器.exe 与控制台版位于同一目录。");

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = baseDir });
            return 0;
        }

        private static CliOptions ParseOptions(string[] args, out string path, out bool pause, out bool json)
        {
            var options = new CliOptions();
            path = null;
            pause = args.Any(a => string.Equals(a, "--pause", StringComparison.OrdinalIgnoreCase));
            json = args.Any(IsJsonFlag);

            for (var i = 1; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg.ToLowerInvariant())
                {
                    case "--output":
                    case "-o":
                        if (i + 1 >= args.Length) throw new PatchException("--output 缺少路径参数。");
                        options.Output = args[++i];
                        break;
                    case "--no-backup":
                        options.NoBackup = true;
                        break;
                    case "--no-verify":
                        options.NoVerify = true;
                        break;
                    case "--dry-run":
                    case "-n":
                        options.DryRun = true;
                        break;
                    case "--pause":
                    case "--json":
                    case "--yes":
                    case "-y":
                    case "--accept-disclaimer":
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                            throw new PatchException("未知参数：" + arg);
                        if (path != null)
                            throw new PatchException("只能指定一个目标文件。");
                        path = arg;
                        break;
                }
            }
            return options;
        }

        private static string FindPcl()
        {
            var candidates = new List<string>
            {
                Path.Combine(Directory.GetCurrentDirectory(), "Plain Craft Launcher 2.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plain Craft Launcher 2.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Plain Craft Launcher 2.exe")
            };
            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch
                {
                }
            }
            return null;
        }

        private static string VerifyJson(PclTargetInfo info)
        {
            var requiresPatch = info.State == PatchState.Original || info.State == PatchState.Partial;
            var ok = info.State != PatchState.Unsupported && info.State != PatchState.Error;
            var builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"schema\": " + SchemaVersion + ",");
            builder.AppendLine("  \"ok\": " + Bool(ok) + ",");
            builder.AppendLine("  \"exitCode\": " + (ok ? 0 : 2) + ",");
            builder.AppendLine("  \"command\": \"" + (_command == "info" ? "info" : "verify") + "\",");
            builder.AppendLine("  \"path\": \"" + JsonEscape(info.Path) + "\",");
            builder.AppendLine("  \"size\": " + info.Size.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"sha256\": \"" + info.Sha256 + "\",");
            builder.AppendLine("  \"fileVersion\": \"" + JsonEscape(info.FileVersion) + "\",");
            builder.AppendLine("  \"assemblyVersion\": \"" + JsonEscape(info.AssemblyVersion) + "\",");
            builder.AppendLine("  \"build\": \"" + JsonEscape(info.BuildLabel) + "\",");
            builder.AppendLine("  \"versionCode\": " + (info.VersionCode.HasValue ? info.VersionCode.Value.ToString(CultureInfo.InvariantCulture) : "null") + ",");
            builder.AppendLine("  \"buildType\": " + (info.BuildType.HasValue ? info.BuildType.Value.ToString(CultureInfo.InvariantCulture) : "null") + ",");
            builder.AppendLine("  \"buildKey\": " + JsonStringOrNull(info.BuildKey) + ",");
            builder.AppendLine("  \"versionText\": " + JsonStringOrNull(info.VersionText) + ",");
            builder.AppendLine("  \"state\": \"" + info.State + "\",");
            builder.AppendLine("  \"stateText\": \"" + JsonEscape(info.StateText) + "\",");
            builder.AppendLine("  \"message\": \"" + JsonEscape(info.Message) + "\",");
            builder.AppendLine("  \"requiresPatch\": " + Bool(requiresPatch) + ",");
            builder.AppendLine("  \"noFixNeeded\": " + Bool(info.NoFixNeeded) + ",");
            builder.AppendLine("  \"backupExists\": " + Bool(info.BackupExists) + ",");
            builder.AppendLine("  \"backupIsValidOriginal\": " + Bool(info.BackupIsValidOriginal) + ",");
            builder.AppendLine("  \"backupFromManifest\": " + Bool(info.BackupFromManifest) + ",");
            builder.AppendLine("  \"backupAssemblyVersion\": " + JsonStringOrNull(info.BackupAssemblyVersion) + ",");
            builder.AppendLine("  \"backupPath\": " + JsonStringOrNull(info.BackupPath) + ",");
            builder.AppendLine("  \"isPreRestriction\": " + Bool(info.IsPreRestriction) + ",");
            builder.AppendLine("  \"autoUpdate\": {");
            builder.AppendLine("    \"state\": \"" + info.AutoUpdateState + "\",");
            builder.AppendLine("    \"text\": \"" + JsonEscape(info.AutoUpdateText) + "\",");
            builder.AppendLine("    \"disabled\": " + Bool(info.AutoUpdateDisabled) + ",");
            builder.AppendLine("    \"configured\": " + Bool(info.AutoUpdateState != AutoUpdateState.NotConfigured) + ",");
            builder.AppendLine("    \"value\": " +
                (info.AutoUpdateValue.HasValue
                    ? info.AutoUpdateValue.Value.ToString(CultureInfo.InvariantCulture)
                    : "null") + ",");
            builder.AppendLine("    \"setupIni\": " + JsonStringOrNull(info.SetupIniPath) + ",");
            builder.AppendLine("    \"error\": " + JsonStringOrNull(info.AutoUpdateError));
            builder.AppendLine("  }");
            builder.AppendLine("}");
            return builder.ToString().TrimEnd();
        }

        private static string PatchJson(PatchResult result)
        {
            var builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"schema\": " + SchemaVersion + ",");
            builder.AppendLine("  \"ok\": true,");
            builder.AppendLine("  \"exitCode\": 0,");
            builder.AppendLine("  \"command\": \"patch\",");
            builder.AppendLine("  \"target\": " + JsonStringOrNull(result.TargetPath) + ",");
            builder.AppendLine("  \"output\": " + JsonStringOrNull(result.OutputPath) + ",");
            builder.AppendLine("  \"backup\": " + JsonStringOrNull(result.BackupPath) + ",");
            builder.AppendLine("  \"alreadyPatched\": " + Bool(result.AlreadyPatched) + ",");
            builder.AppendLine("  \"dryRun\": " + Bool(result.DryRun) + ",");
            builder.AppendLine("  \"originalSha256\": " + JsonStringOrNull(result.OriginalSha256) + ",");
            builder.AppendLine("  \"patchedSha256\": " + JsonStringOrNull(result.PatchedSha256) + ",");
            builder.AppendLine("  \"restoredMethods\": [" +
                string.Join(", ", result.RestoredMethods.Select(m => "\"" + JsonEscape(m) + "\"")) + "],");
            builder.AppendLine("  \"elapsedMs\": " + ((long)result.Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"message\": " + JsonStringOrNull(result.Message));
            builder.AppendLine("}");
            return builder.ToString().TrimEnd();
        }

        private static string RestoreJson(PatchResult result)
        {
            var builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"schema\": " + SchemaVersion + ",");
            builder.AppendLine("  \"ok\": true,");
            builder.AppendLine("  \"exitCode\": 0,");
            builder.AppendLine("  \"command\": \"restore\",");
            builder.AppendLine("  \"target\": " + JsonStringOrNull(result.TargetPath) + ",");
            builder.AppendLine("  \"output\": " + JsonStringOrNull(result.OutputPath) + ",");
            builder.AppendLine("  \"backup\": " + JsonStringOrNull(result.BackupPath) + ",");
            builder.AppendLine("  \"alreadyOriginal\": " + Bool(result.AlreadyOriginal) + ",");
            builder.AppendLine("  \"dryRun\": " + Bool(result.DryRun) + ",");
            builder.AppendLine("  \"originalSha256\": " + JsonStringOrNull(result.OriginalSha256) + ",");
            builder.AppendLine("  \"patchedSha256\": " + JsonStringOrNull(result.PatchedSha256) + ",");
            builder.AppendLine("  \"elapsedMs\": " + ((long)result.Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"message\": " + JsonStringOrNull(result.Message));
            builder.AppendLine("}");
            return builder.ToString().TrimEnd();
        }

        private static string AutoUpdateJson(AutoUpdateChange result, bool disable)
        {
            var builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"schema\": " + SchemaVersion + ",");
            builder.AppendLine("  \"ok\": true,");
            builder.AppendLine("  \"exitCode\": 0,");
            builder.AppendLine("  \"command\": \"" + (disable ? "disable-update" : "enable-update") + "\",");
            builder.AppendLine("  \"target\": " + JsonStringOrNull(result.TargetPath) + ",");
            builder.AppendLine("  \"setupIni\": " + JsonStringOrNull(result.SetupIniPath) + ",");
            builder.AppendLine("  \"changed\": " + Bool(result.Changed) + ",");
            builder.AppendLine("  \"alreadyInState\": " + Bool(result.AlreadyInState) + ",");
            builder.AppendLine("  \"previousValue\": " + JsonStringOrNull(result.PreviousValue) + ",");
            builder.AppendLine("  \"currentValue\": " + JsonStringOrNull(result.CurrentValue) + ",");
            builder.AppendLine("  \"backup\": " + JsonStringOrNull(result.BackupPath) + ",");
            builder.AppendLine("  \"message\": " + JsonStringOrNull(result.Message));
            builder.AppendLine("}");
            return builder.ToString().TrimEnd();
        }

        private static string SelfCheckJson(SelfCheckResult result)
        {
            var builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"schema\": " + SchemaVersion + ",");
            builder.AppendLine("  \"ok\": " + Bool(result.AllEmbeddedOk) + ",");
            builder.AppendLine("  \"exitCode\": " + (result.AllEmbeddedOk ? 0 : 1) + ",");
            builder.AppendLine("  \"command\": \"selfcheck\",");
            builder.AppendLine("  \"path\": \"" + JsonEscape(result.ExePath) + "\",");
            builder.AppendLine("  \"size\": " + result.Size.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"sha256\": \"" + result.ExeSha256 + "\",");
            builder.AppendLine("  \"donorOk\": " + Bool(result.DonorOk) + ",");
            builder.AppendLine("  \"donorSha256\": \"" + result.DonorSha256 + "\",");
            builder.AppendLine("  \"cecilOk\": " + Bool(result.CecilOk) + ",");
            builder.AppendLine("  \"cecilSha256\": \"" + result.CecilSha256 + "\",");
            builder.AppendLine("  \"projectUrl\": \"" + JsonEscape(result.ProjectUrl) + "\"");
            builder.AppendLine("}");
            return builder.ToString().TrimEnd();
        }

        private static string ErrorJson(string command, string message, int exitCode)
        {
            var builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"schema\": " + SchemaVersion + ",");
            builder.AppendLine("  \"ok\": false,");
            builder.AppendLine("  \"command\": \"" + JsonEscape(command ?? "") + "\",");
            builder.AppendLine("  \"error\": \"" + JsonEscape(message ?? "") + "\",");
            builder.AppendLine("  \"exitCode\": " + exitCode.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("}");
            return builder.ToString().TrimEnd();
        }

        private static void WriteJson(string json)
        {
            _stdout.WriteLine(json);
            _stdout.Flush();
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string JsonStringOrNull(string value)
        {
            return value == null ? "null" : "\"" + JsonEscape(value) + "\"";
        }

        private static string JsonEscape(string text)
        {
            if (text == null) return "";
            var builder = new StringBuilder(text.Length + 8);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(c);
                        break;
                }
            }
            return builder.ToString();
        }

        private static void PrintLog(LogEntry entry)
        {
            lock (ConsoleLock)
            {
                var previous = Console.ForegroundColor;
                Console.ForegroundColor = ColorFor(entry.Level);
                Console.WriteLine(entry.ToLine());
                Console.ForegroundColor = previous;
            }
        }

        private static ConsoleColor ColorFor(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Step: return ConsoleColor.Cyan;
                case LogLevel.Success: return ConsoleColor.Green;
                case LogLevel.Warn: return ConsoleColor.Yellow;
                case LogLevel.Error: return ConsoleColor.Red;
                case LogLevel.Detail: return ConsoleColor.DarkGray;
                default: return ConsoleColor.Gray;
            }
        }

        private static void PrintBanner()
        {
            if (_json) return;
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("══════════════════════════════════════════════════════════");
            Console.WriteLine("  PCL 下载修复器（第三方） · 控制台版 v" + DisclaimerText.ProductVersion);
            Console.WriteLine("  Ethernos Studio · 恢复 PCL 2.99 的 Minecraft 下载功能");
            Console.WriteLine("══════════════════════════════════════════════════════════");
            Console.ForegroundColor = previous;
            Console.WriteLine();
        }

        private static void PrintHelp()
        {
            Console.WriteLine("用法：");
            Console.WriteLine("  PCLDownloadFix.Console.exe patch   [EXE] [选项]   应用下载修复");
            Console.WriteLine("  PCLDownloadFix.Console.exe restore [EXE] [选项]   还原为原版");
            Console.WriteLine("  PCLDownloadFix.Console.exe verify  [EXE] [--json]  检查文件状态");
            Console.WriteLine("  PCLDownloadFix.Console.exe info    [EXE] [--json]  显示详细信息");
            Console.WriteLine("  PCLDownloadFix.Console.exe disable-update [EXE]   关闭 PCL 自动更新（停留当前版本）");
            Console.WriteLine("  PCLDownloadFix.Console.exe enable-update  [EXE]   恢复 PCL 自动更新");
            Console.WriteLine("  PCLDownloadFix.Console.exe hmcl-info   <JAR|EXE>  查看 HMCL 地区限制状态");
            Console.WriteLine("  PCLDownloadFix.Console.exe hmcl-patch  <JAR|EXE>  修复 HMCL 下载限制");
            Console.WriteLine("  PCLDownloadFix.Console.exe hmcl-restore <JAR|EXE> 恢复 HMCL 原版");
            Console.WriteLine("  PCLDownloadFix.Console.exe fcl-patch   <APK>     生成 FCL 共存修复版（需 Java）");
            Console.WriteLine("  PCLDownloadFix.Console.exe selfcheck [--json]     校验本程序完整性");
            Console.WriteLine("  PCLDownloadFix.Console.exe gui                    打开图形界面");
            Console.WriteLine("  PCLDownloadFix.Console.exe --version              显示版本");
            Console.WriteLine();
            Console.WriteLine("选项：");
            Console.WriteLine("  -o, --output <路径>   输出到副本，不修改原文件");
            Console.WriteLine("      --no-backup       不创建 .pcl-original.bak 原版备份");
            Console.WriteLine("      --no-verify       跳过写入后的 IL 一致性校验");
            Console.WriteLine("  -n, --dry-run         只分析并模拟修复，不写文件");
            Console.WriteLine("  -y, --yes             确认免责声明（非交互环境必须）");
            Console.WriteLine("      --json            以 JSON 输出结果；stdout 只含 JSON，日志走 stderr");
            Console.WriteLine("      --pause           执行结束后等待按键");
            Console.WriteLine();
            Console.WriteLine("不指定 EXE 时，会在当前目录和程序目录自动查找 Plain Craft Launcher 2.exe。");
            Console.WriteLine("执行 patch/restore/verify/info/disable-update/enable-update/hmcl-patch/hmcl-restore 前会显示免责声明并要求确认。");
            Console.WriteLine("退出码：0 成功；1 失败；2 不支持的文件；3 未确认免责声明。");
            Console.WriteLine("建议先完全关闭 PCL。");
        }

        private static void Header(string title)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("── " + title + " " + new string('─', Math.Max(0, 56 - title.Length)));
            Console.ForegroundColor = previous;
            Console.WriteLine();
        }

        private static void Success(string message) { WriteColor(ConsoleColor.Green, "✔ " + message); }
        private static void Warning(string message) { WriteColor(ConsoleColor.Yellow, "! " + message); }
        private static void Error(string message) { WriteColor(ConsoleColor.Red, "✘ " + message); }

        private static void WriteColor(ConsoleColor color, string message)
        {
            lock (ConsoleLock)
            {
                var previous = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(message);
                Console.ForegroundColor = previous;
            }
        }

        private static void WaitIfInteractive()
        {
            if (_json) return;
            if (!Console.IsInputRedirected)
            {
                Console.WriteLine();
                Console.WriteLine("按任意键退出...");
                Console.ReadKey(true);
            }
        }

        private static void WaitIfRequested(bool pause)
        {
            if (_json || !pause) return;
            Console.WriteLine();
            Console.WriteLine("按任意键退出...");
            Console.ReadKey(true);
        }

        private sealed class CliOptions
        {
            public string Output;
            public bool NoBackup;
            public bool NoVerify;
            public bool DryRun;
        }
    }
}
