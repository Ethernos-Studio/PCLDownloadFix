using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace PCLDownloadFix.Tests
{
    /// <summary>
    /// Smoke tests for the machine-facing CLI contract: --json must put exactly one JSON
    /// document on stdout and every human-facing line on stderr.
    /// </summary>
    public class CliContractTests
    {
        [Fact]
        public void VerifyJson_OnInvalidFile_EmitsPureJsonError()
        {
            var cli = FindCli();
            if (cli == null) return;

            using (var workspace = new TempWorkspace())
            {
                var bad = workspace.Path("not-pcl.txt");
                File.WriteAllText(bad, "not a .NET assembly");

                var result = Run(cli, "verify \"" + bad + "\" --yes --json");

                Assert.Equal(1, result.ExitCode);
                Assert.StartsWith("{", result.StdOut.TrimStart());
                Assert.Contains("\"ok\": false", result.StdOut);
                Assert.Contains("\"command\": \"verify\"", result.StdOut);
                Assert.Contains("\"exitCode\": 1", result.StdOut);
                Assert.DoesNotContain("PCL 下载修复器", result.StdOut);
                Assert.Equal("", result.StdErr.Trim());
            }
        }

        [Fact]
        public void VerifyJson_WithoutYes_RefusesWithExitCode3AndJson()
        {
            var cli = FindCli();
            if (cli == null) return;

            var result = Run(cli, "verify \"C:\\does-not-matter.exe\" --json");

            Assert.Equal(3, result.ExitCode);
            Assert.StartsWith("{", result.StdOut.TrimStart());
            Assert.Contains("\"ok\": false", result.StdOut);
            Assert.Contains("\"exitCode\": 3", result.StdOut);
            Assert.Equal("", result.StdErr.Trim());
        }

        [Fact]
        public void UnsupportedBuild_ReturnsExitCode2InJson()
        {
            var cli = FindCli();
            if (cli == null) return;

            using (var workspace = new TempWorkspace())
            {
                var pcl = SyntheticPcl.Write(workspace.Path("PCL.exe"), true, omitMethods: true);

                var verify = Run(cli, "verify \"" + pcl + "\" --yes --json");
                Assert.Equal(2, verify.ExitCode);
                Assert.Contains("\"state\": \"Unsupported\"", verify.StdOut);

                var patch = Run(cli, "patch \"" + pcl + "\" --yes --json");
                Assert.Equal(2, patch.ExitCode);
                Assert.Contains("\"ok\": false", patch.StdOut);
                Assert.Contains("\"exitCode\": 2", patch.StdOut);
                Assert.DoesNotContain("\"ok\": false", patch.StdErr);
            }
        }

        [Fact]
        public void DisableUpdateJson_OnReal412_UpdatesConfigAndVerifyReportsIt()
        {
            var cli = FindCli();
            var pcl = Environment.GetEnvironmentVariable("PCLDF_TEST_PRE_412");
            if (cli == null || string.IsNullOrWhiteSpace(pcl)) return;

            using (var workspace = new TempWorkspace())
            {
                var target = workspace.Path("PCL.exe");
                File.Copy(pcl.Trim('"'), target);

                var disable = Run(cli, "disable-update \"" + target + "\" --yes --json");
                Assert.Equal(0, disable.ExitCode);
                Assert.Contains("\"command\": \"disable-update\"", disable.StdOut);
                Assert.Contains("\"changed\": true", disable.StdOut);

                var verify = Run(cli, "verify \"" + target + "\" --yes --json");
                Assert.Equal(0, verify.ExitCode);
                Assert.Contains("\"disabled\": true", verify.StdOut);
                Assert.Contains("\"autoUpdate\"", verify.StdOut);

                var enable = Run(cli, "enable-update \"" + target + "\" --yes --json");
                Assert.Equal(0, enable.ExitCode);
                Assert.Contains("\"command\": \"enable-update\"", enable.StdOut);
            }
        }

        [Fact]
        public void VerifyJson_OnReal414_EmitsStateWithoutBanner()
        {
            var cli = FindCli();
            var pcl = Environment.GetEnvironmentVariable("PCLDF_TEST_ORIGINAL_414");
            if (cli == null || string.IsNullOrWhiteSpace(pcl)) return;

            var result = Run(cli, "verify \"" + pcl.Trim('"') + "\" --yes --json");

            Assert.Equal(0, result.ExitCode);
            Assert.StartsWith("{", result.StdOut.TrimStart());
            Assert.Contains("\"state\": \"Original\"", result.StdOut);
            Assert.Contains("\"requiresPatch\": true", result.StdOut);
            Assert.DoesNotContain("PCL 下载修复器", result.StdOut);
            Assert.Equal("", result.StdErr.Trim());
        }

        private static string FindCli()
        {
            var fromEnv = Environment.GetEnvironmentVariable("PCLDF_TEST_CLI");
            if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return Path.GetFullPath(fromEnv);

            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "Cli", "bin", "Release", "net48", "PCLDownloadFix.Console.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "Cli", "bin", "Debug", "net48", "PCLDownloadFix.Console.exe")),
                Path.Combine(baseDir, "PCLDownloadFix.Console.exe")
            };
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static ProcessResult Run(string exe, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            using (var process = Process.Start(startInfo))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                Assert.True(process.WaitForExit(60000), "CLI process timed out");
                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StdOut = stdout.Result,
                    StdErr = stderr.Result
                };
            }
        }

        private sealed class ProcessResult
        {
            public int ExitCode;
            public string StdOut;
            public string StdErr;
        }
    }
}
