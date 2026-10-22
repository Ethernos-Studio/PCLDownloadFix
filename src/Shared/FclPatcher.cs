using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PCLDownloadFix
{
    public sealed class FclPatchOptions
    {
        public string ToolsDirectory { get; set; }
        public string JavaPath { get; set; }
        public bool AllowWithoutGate { get; set; }
        public Action<string> Log { get; set; }
    }

    public sealed class FclPatchResult
    {
        public bool Success { get; set; }
        public string SourcePath { get; set; }
        public string OutputPath { get; set; }
        public string WorkDirectory { get; set; }
        public string LogPath { get; set; }
        public string Message { get; set; }
        public IList<string> Steps { get; set; }
    }

    /// <summary>
    /// FCL coexistence patcher.
    ///
    /// FCL is an Android APK, so this intentionally delegates the heavy Android
    /// packaging work to bundled apktool + uber-apk-signer. The result uses a new
    /// package id (com.tungsten.fcl.fix), label "FCL (Fix)" and a new signature,
    /// so it can be installed next to the official FCL.
    /// </summary>
    public static class FclPatcher
    {
        public const string OriginalPackage = "com.tungsten.fcl";
        public const string FixedPackage = "com.tungsten.fcl.fix";
        public const string FixedLabel = "FCL (Fix)";

        private static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";

        public static FclPatchResult Patch(string apkPath, FclPatchOptions options)
        {
            options = options ?? new FclPatchOptions();
            var result = new FclPatchResult
            {
                SourcePath = apkPath,
                Steps = new List<string>()
            };

            var work = Path.Combine(Path.GetTempPath(), "PCLDownloadFix-fcl-" + Guid.NewGuid().ToString("N"));
            result.WorkDirectory = work;
            try
            {
                ValidateApk(apkPath);
                var tools = ResolveTools(options);
                var decode = Path.Combine(work, "decode");
                var unsigned = Path.Combine(work, "unsigned.apk");
                var signedDir = Path.Combine(work, "signed");
                Directory.CreateDirectory(work);
                result.LogPath = Path.Combine(work, "fcl-patch.log");
                Log(options, "FCL 共存修复开始：" + apkPath);
                Log(options, "工具目录：" + tools.Directory);
                Log(options, "Java：" + tools.Java);

                Step(result, options, "解码 APK（apktool）...");
                Run(tools.Java, new[]
                {
                    "-jar", tools.ApkTool, "d", "-f", "-o", decode, apkPath
                }, work, options);

                var manifestPath = Path.Combine(decode, "AndroidManifest.xml");
                if (!File.Exists(manifestPath))
                    throw new InvalidDataException("apktool 没有生成 AndroidManifest.xml。");
                var version = ReadApktoolVersion(decode);
                if (!string.IsNullOrEmpty(version)) Step(result, options, "FCL 版本：" + version);

                Step(result, options, "修改包名、应用名和组件引用...");
                PatchManifest(manifestPath);
                PatchApktoolMetadata(decode);

                Step(result, options, "修改 RegionUtils 与包名常量...");
                PatchSmali(decode, options.AllowWithoutGate);

                Step(result, options, "修改应用名称资源...");
                PatchResources(decode);

                Step(result, options, "重新打包 APK...");
                Run(tools.Java, new[]
                {
                    "-jar", tools.ApkTool, "b", decode, "-o", unsigned
                }, work, options);
                if (!File.Exists(unsigned))
                    throw new InvalidDataException("apktool 没有生成未签名 APK。");

                Step(result, options, "使用新证书签名（v1/v2/v3）...");
                Directory.CreateDirectory(signedDir);
                Run(tools.Java, new[]
                {
                    "-jar", tools.Signer, "-a", unsigned, "-o", signedDir,
                    "--allowResign", "--debug", "--skipZipAlign"
                }, work, options);

                var produced = Directory.GetFiles(signedDir, "*.apk", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (produced == null)
                    throw new InvalidDataException("签名工具没有生成 APK。");

                var output = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(apkPath)) ?? ".",
                    Path.GetFileNameWithoutExtension(apkPath) + "-fix.apk");
                File.Copy(produced, output, true);
                result.OutputPath = output;
                result.Success = true;
                result.Message = "已生成共存版：" + output;
                Step(result, options, result.Message);
                Log(options, result.Message);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
                Log(options, "FCL 共存修复失败：" + ex);
                return result;
            }
        }

        private static void ValidateApk(string apkPath)
        {
            if (string.IsNullOrWhiteSpace(apkPath) || !File.Exists(apkPath))
                throw new FileNotFoundException("找不到 FCL APK。", apkPath);
            using (var archive = ZipFile.OpenRead(apkPath))
            {
                if (archive.GetEntry("AndroidManifest.xml") == null)
                    throw new InvalidDataException("不是有效的 APK：缺少 AndroidManifest.xml。");
                if (!archive.Entries.Any(e => e.FullName.StartsWith("classes", StringComparison.OrdinalIgnoreCase) &&
                                              e.FullName.EndsWith(".dex", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("不是有效的 APK：缺少 classes*.dex。");
            }
        }

        private static void PatchManifest(string manifestPath)
        {
            var doc = XDocument.Load(manifestPath, LoadOptions.PreserveWhitespace);
            var root = doc.Root;
            if (root == null) throw new InvalidDataException("AndroidManifest.xml 为空。");

            var package = root.Attribute("package");
            if (package == null || !string.Equals(package.Value, OriginalPackage, StringComparison.Ordinal))
                throw new InvalidDataException("这不是预期的 FCL 包名：" + (package == null ? "(missing)" : package.Value));
            package.Value = FixedPackage;

            foreach (var element in root.DescendantsAndSelf())
            {
                foreach (var attribute in element.Attributes().ToList())
                {
                    var name = attribute.Name.LocalName;
                    if (attribute.Name.Namespace != Android && name != "package") continue;

                    if (name == "label")
                    {
                        attribute.Value = FixedLabel;
                        continue;
                    }
                    if (name == "versionName")
                    {
                        attribute.Value = attribute.Value + " (Fix)";
                        continue;
                    }
                    if (name == "authorities" || name == "taskAffinity")
                    {
                        attribute.Value = ReplacePackageToken(
                            attribute.Value.Replace("${applicationId}", FixedPackage));
                        continue;
                    }
                    if (name == "name" || name == "targetActivity" || name == "backupAgent" ||
                        name == "manageSpaceActivity" || name == "parentActivityName" ||
                        name == "appComponentFactory")
                    {
                        if (attribute.Value.StartsWith(".", StringComparison.Ordinal))
                            attribute.Value = OriginalPackage + attribute.Value;
                    }
                }
                if (element.Name.LocalName == "application")
                {
                    var label = element.Attribute(Android + "label");
                    if (label != null) label.Value = FixedLabel;
                }
            }

            doc.Save(manifestPath);
        }

        private static string ReadApktoolVersion(string decodeDir)
        {
            var path = Path.Combine(decodeDir, "apktool.yml");
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadAllLines(path))
            {
                var index = line.IndexOf("versionName:", StringComparison.Ordinal);
                if (index >= 0) return line.Substring(index + "versionName:".Length).Trim();
            }
            return null;
        }

        private static void PatchApktoolMetadata(string decodeDir)
        {
            var path = Path.Combine(decodeDir, "apktool.yml");
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var index = line.IndexOf("versionName:", StringComparison.Ordinal);
                if (index < 0) continue;
                var prefix = line.Substring(0, index + "versionName:".Length);
                var value = line.Substring(prefix.Length).Trim();
                if (!value.EndsWith("(Fix)", StringComparison.OrdinalIgnoreCase))
                    lines[i] = prefix + " " + value + " (Fix)";
                break;
            }
            File.WriteAllLines(path, lines);
        }

        private static void PatchSmali(string decodeDir, bool allowWithoutGate)
        {
            var smaliDirs = Directory.GetDirectories(decodeDir, "smali*", SearchOption.TopDirectoryOnly);
            var region = smaliDirs
                .Select(d => Path.Combine(d, "com", "tungsten", "fcl", "util", "RegionUtils.smali"))
                .FirstOrDefault(File.Exists);
            if (region == null)
            {
                if (!allowWithoutGate)
                    throw new InvalidDataException(
                        "找不到 RegionUtils.smali；该 FCL 版本可能还没有地区下载限制（无需修复），" +
                        "或版本结构已变化。");
            }
            else
            {
                var text = File.ReadAllText(region);
                text = ReplaceMethod(text, "isChinaMainlandNetwork", "()Z",
                    ".registers 1\r\n    const/4 v0, 0x0\r\n    return v0", true);
                text = ReplaceMethod(text, "isChinaMainlandMachine", "()Z",
                    ".registers 1\r\n    const/4 v0, 0x0\r\n    return v0", false);
                text = ReplaceMethod(text, "init", "()V",
                    ".registers 0\r\n    return-void", false);
                text = ReplaceMethod(text, "chinaMainlandNetworkAsync",
                    "()Ljava/util/concurrent/CompletableFuture;",
                    ".registers 1\r\n    const/4 v0, 0x0\r\n    invoke-static {v0}, Ljava/lang/Boolean;->valueOf(Z)Ljava/lang/Boolean;\r\n" +
                    "    move-result-object v0\r\n    invoke-static {v0}, Ljava/util/concurrent/CompletableFuture;->completedFuture(Ljava/lang/Object;)Ljava/util/concurrent/CompletableFuture;\r\n" +
                    "    move-result-object v0\r\n    return-object v0", false);
                text = ReplaceMethod(text, "observeChinaMainlandNetwork",
                    "(Ljava/util/function/Consumer;)V",
                    ".registers 2\r\n    const/4 v0, 0x0\r\n    invoke-static {v0}, Ljava/lang/Boolean;->valueOf(Z)Ljava/lang/Boolean;\r\n" +
                    "    move-result-object v0\r\n    invoke-interface {p0, v0}, Ljava/util/function/Consumer;->accept(Ljava/lang/Object;)V\r\n    return-void", false);
                File.WriteAllText(region, text);
            }

            // BuildConfig.APPLICATION_ID and any literal package strings used for
            // FileProvider / intents / data paths.
            foreach (var file in Directory.GetFiles(decodeDir, "*.smali", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(file);
                if (!source.Contains(OriginalPackage)) continue;
                source = ReplacePackageToken(source);
                File.WriteAllText(file, source);
            }
        }

        internal static string ReplaceMethod(string source, string name, string descriptor, string body,
            bool required = true)
        {
            var pattern = @"(?s)\.method[^\r\n]*\b" + Regex.Escape(name) +
                          Regex.Escape(descriptor) + @"[^\r\n]*\r?\n.*?\.end method";
            var replacement = ".method public static " + name + descriptor + "\r\n    " + body + "\r\n.end method";
            var result = Regex.Replace(source, pattern, replacement);
            if (result == source && required)
                throw new InvalidDataException("找不到 RegionUtils." + name + descriptor + "。");
            return result;
        }

        private static void PatchResources(string decodeDir)
        {
            var resDir = Path.Combine(decodeDir, "res");
            if (!Directory.Exists(resDir)) return;
            var values = Directory.GetDirectories(resDir, "values*", SearchOption.TopDirectoryOnly);
            foreach (var dir in values)
            {
                var file = Path.Combine(dir, "strings.xml");
                if (!File.Exists(file)) continue;
                var doc = XDocument.Load(file, LoadOptions.PreserveWhitespace);
                foreach (var element in doc.Root.Elements("string"))
                {
                    var name = (string)element.Attribute("name");
                    if (name == "app_name") element.Value = FixedLabel;
                    if (name == "file_browser_provider")
                        element.Value = ReplacePackageToken(
                            element.Value.Replace("${applicationId}", FixedPackage));
                }
                doc.Save(file);
            }
        }

        private static Toolchain ResolveTools(FclPatchOptions options)
        {
            var dir = options.ToolsDirectory;
            if (string.IsNullOrEmpty(dir))
            {
                var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                for (var i = 0; i < 8 && current != null; i++, current = current.Parent)
                {
                    var packaged = Path.Combine(current.FullName, "tools", "android");
                    if (File.Exists(Path.Combine(packaged, "apktool.jar"))) { dir = packaged; break; }
                    var source = Path.Combine(current.FullName, "third_party", "android");
                    if (File.Exists(Path.Combine(source, "apktool.jar"))) { dir = source; break; }
                }
                if (string.IsNullOrEmpty(dir)) dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "android");
            }
            dir = Path.GetFullPath(dir);
            var apktool = Path.Combine(dir, "apktool.jar");
            var signer = Path.Combine(dir, "uber-apk-signer.jar");
            if (!File.Exists(apktool) || !File.Exists(signer))
                throw new FileNotFoundException("缺少 Android 工具：" + dir + "\\apktool.jar / uber-apk-signer.jar");

            var java = options.JavaPath;
            if (string.IsNullOrEmpty(java))
            {
                var bundled = Path.Combine(dir, "jre", "bin", "java.exe");
                java = File.Exists(bundled) ? bundled : "java";
            }
            return new Toolchain { Directory = dir, ApkTool = apktool, Signer = signer, Java = java };
        }

        private static void Run(string java, string[] arguments, string workDir, FclPatchOptions options)
        {
            var info = new ProcessStartInfo
            {
                FileName = java,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.Arguments = string.Join(" ", arguments.Select(QuoteArgument));

            Log(options, "运行：" + java + " " + info.Arguments);
            using (var process = new Process { StartInfo = info })
            {
                process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Log(options, e.Data); };
                process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Log(options, e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("命令失败，退出码 " + process.ExitCode + "：" + java + " " +
                        string.Join(" ", arguments));
            }
        }

        internal static string ReplacePackageToken(string value)
        {
            return Regex.Replace(value,
                @"(?<![A-Za-z0-9_])com\.tungsten\.fcl(?![A-Za-z0-9_])",
                FixedPackage);
        }

        private static string QuoteArgument(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void Step(FclPatchResult result, FclPatchOptions options, string text)
        {
            result.Steps.Add(text);
            Log(options, text);
        }

        private static void Log(FclPatchOptions options, string text)
        {
            if (options.Log == null) return;
            try { options.Log(text); } catch { }
        }

        private sealed class Toolchain
        {
            public string Directory;
            public string ApkTool;
            public string Signer;
            public string Java;
        }
    }
}
