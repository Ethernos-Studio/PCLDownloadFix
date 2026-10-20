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
        private const string Version = "1.0.0";
        private const string Author = "Ethernos Studio";
        private static readonly object ConsoleLock = new object();

        public static int Run(string[] args)
        {
            Console.Title = Product + " · 控制台版 v" + Version + " · " + Author;
            if (args == null || args.Length == 0)
            {
                PrintBanner();
                PrintHelp();
                WaitIfInteractive();
                return 0;
            }

            var command = args[0].Trim().ToLowerInvariant();
            try
            {
                switch (command)
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
                        Console.WriteLine(Product + " v" + Version + " · " + Author);
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

                if (command == "patch" || command == "apply" || command == "fix")
                {
                    PrintBanner();
                    if (!EnsureDisclaimer(args)) return 3;
                    return Patch(args);
                }
                if (command == "restore" || command == "revert")
                {
                    PrintBanner();
                    if (!EnsureDisclaimer(args)) return 3;
                    return Restore(args);
                }
                if (command == "verify" || command == "check" || command == "info")
                {
                    PrintBanner();
                    if (!EnsureDisclaimer(args)) return 3;
                    return Verify(args, command == "info");
                }

                if (File.Exists(args[0]))
                {
                    PrintBanner();
                    if (!EnsureDisclaimer(args)) return 3;
                    return Patch(new[] { "patch", args[0] }.Concat(args.Skip(1)).ToArray());
                }

                PrintBanner();
                Error("未知命令：" + args[0]);
                PrintHelp();
                return 1;
            }
            catch (PatchException ex)
            {
                Error(ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Error("发生未预期的错误：" + ex.Message);
                return 1;
            }
        }

        private static bool EnsureDisclaimer(string[] args)
        {
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
            if (args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("{");
                Console.WriteLine("  \"path\": \"" + JsonEscape(result.ExePath) + "\",");
                Console.WriteLine("  \"size\": " + result.Size.ToString(CultureInfo.InvariantCulture) + ",");
                Console.WriteLine("  \"sha256\": \"" + result.ExeSha256 + "\",");
                Console.WriteLine("  \"donorOk\": " + (result.DonorOk ? "true" : "false") + ",");
                Console.WriteLine("  \"cecilOk\": " + (result.CecilOk ? "true" : "false") + ",");
                Console.WriteLine("  \"projectUrl\": \"" + JsonEscape(result.ProjectUrl) + "\"");
                Console.WriteLine("}");
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

        private static int Patch(string[] args)
        {
            var options = ParseOptions(args, out var path, out var pause, out var json);
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
            var options = ParseOptions(args, out var path, out var pause, out var json);
            if (path == null) path = FindPcl();
            if (path == null) throw new PatchException("未找到 PCL，请指定要还原的 EXE 路径。");

            Header("还原原版");
            Console.WriteLine("目标文件：" + Path.GetFullPath(path));
            Console.WriteLine();

            var patcher = new Patcher(PrintLog);
            var result = patcher.Restore(path, options.DryRun);
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

        private static int Verify(string[] args, bool infoOnly)
        {
            var options = ParseOptions(args, out var path, out var pause, out var json);
            if (path == null) path = FindPcl();
            if (path == null) throw new PatchException("未找到 PCL，请指定要检查的 EXE 路径。");

            var patcher = new Patcher(PrintLog);
            var info = patcher.Inspect(path);

            if (json)
            {
                PrintJson(info);
            }
            else
            {
                Header(infoOnly ? "文件信息" : "校验结果");
                Console.WriteLine("文件路径  : " + info.Path);
                Console.WriteLine("文件大小  : " + HashUtil.FormatSize(info.Size));
                Console.WriteLine("文件版本  : " + (string.IsNullOrEmpty(info.FileVersion) ? info.AssemblyVersion : info.FileVersion));
                Console.WriteLine("构建识别  : " + info.BuildLabel);
                Console.WriteLine("补丁状态  : " + info.StateText);
                Console.WriteLine("状态说明  : " + info.Message);
                Console.WriteLine("SHA-256   : " + info.Sha256);
                Console.WriteLine("原版备份  : " + info.BackupText);
                if (info.BackupExists) Console.WriteLine("备份 SHA  : " + info.BackupSha256);
                Console.WriteLine();

                if (info.State == PatchState.Patched)
                    Success(info.NoFixNeeded
                        ? "该版本是下载限制前的官方实现，已具备完整下载功能，无需修复。"
                        : "此文件已应用下载修复。");
                else if (info.State == PatchState.Original)
                    Warning("此文件是受限原版，可执行 patch 命令应用修复。");
                else if (info.State == PatchState.Partial)
                    Warning("此文件只有部分修复，可重新执行 patch 进行完整修复。");
                else
                    Error("不支持或无法识别此文件。");
            }

            WaitIfRequested(pause);
            return info.State == PatchState.Unsupported ? 2 : 0;
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
            json = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));

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

        private static void PrintJson(PclTargetInfo info)
        {
            var builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"path\": \"" + JsonEscape(info.Path) + "\",");
            builder.AppendLine("  \"size\": " + info.Size.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"sha256\": \"" + info.Sha256 + "\",");
            builder.AppendLine("  \"fileVersion\": \"" + JsonEscape(info.FileVersion) + "\",");
            builder.AppendLine("  \"assemblyVersion\": \"" + JsonEscape(info.AssemblyVersion) + "\",");
            builder.AppendLine("  \"build\": \"" + JsonEscape(info.BuildLabel) + "\",");
            builder.AppendLine("  \"state\": \"" + info.State + "\",");
            builder.AppendLine("  \"stateText\": \"" + JsonEscape(info.StateText) + "\",");
            builder.AppendLine("  \"message\": \"" + JsonEscape(info.Message) + "\",");
            builder.AppendLine("  \"backupExists\": " + (info.BackupExists ? "true" : "false") + ",");
            builder.AppendLine("  \"backupIsValidOriginal\": " + (info.BackupIsValidOriginal ? "true" : "false") + ",");
            builder.AppendLine("  \"backupPath\": \"" + JsonEscape(info.BackupPath) + "\"");
            builder.AppendLine("}");
            Console.WriteLine(builder.ToString());
        }

        private static string JsonEscape(string text)
        {
            if (text == null) return "";
            return text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
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
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("══════════════════════════════════════════════════════════");
            Console.WriteLine("  PCL 下载修复器（第三方） · 控制台版 v1.0");
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
            Console.WriteLine("      --json            以 JSON 输出 verify/info/selfcheck 结果");
            Console.WriteLine("      --pause           执行结束后等待按键");
            Console.WriteLine();
            Console.WriteLine("不指定 EXE 时，会在当前目录和程序目录自动查找 Plain Craft Launcher 2.exe。");
            Console.WriteLine("执行 patch/restore/verify/info 前会显示免责声明并要求确认。");
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
            if (!Console.IsInputRedirected)
            {
                Console.WriteLine();
                Console.WriteLine("按任意键退出...");
                Console.ReadKey(true);
            }
        }

        private static void WaitIfRequested(bool pause)
        {
            if (!pause) return;
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


