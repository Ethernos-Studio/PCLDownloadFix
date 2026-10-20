using System.IO;
using System.Text;
using Xunit;

namespace PCLDownloadFix.Tests
{
    public class AutoUpdateSettingsTests
    {
        [Fact]
        public void MissingSetupIni_IsReportedAsUnconfiguredAndCanBeDisabled()
        {
            using (var workspace = new TempWorkspace())
            {
                var exe = CreatePcl(workspace);

                var before = AutoUpdateSettings.Read(exe);
                Assert.Equal(AutoUpdateState.NotConfigured, before.State);
                Assert.False(before.Disabled);

                var change = AutoUpdateSettings.Disable(exe, true, null);

                Assert.True(change.Changed);
                Assert.False(string.IsNullOrEmpty(change.SetupIniPath));
                Assert.True(File.Exists(change.SetupIniPath));

                var after = AutoUpdateSettings.Read(exe);
                Assert.True(after.Disabled);
                Assert.Equal(3, after.Value);
                Assert.Contains("SystemSystemUpdate:3", File.ReadAllText(change.SetupIniPath));
            }
        }

        [Fact]
        public void DisableAndEnable_PreserveOtherSettingsAndPreviousValue()
        {
            using (var workspace = new TempWorkspace())
            {
                var exe = CreatePcl(workspace);
                var setup = AutoUpdateSettings.GetSetupIniPath(exe);
                Directory.CreateDirectory(Path.GetDirectoryName(setup));
                var original = "LaunchFolderSelect:E:\\MCLDownload\\Game\\.minecraft\\\r\n" +
                               "SystemSystemUpdate:1\r\n" +
                               "OtherKey:value\r\n";
                File.WriteAllText(setup, original, new UTF8Encoding(false));

                var disable = AutoUpdateSettings.Disable(exe, true, null);

                Assert.True(disable.Changed);
                Assert.Equal("1", disable.PreviousValue);
                var disabledText = File.ReadAllText(setup);
                Assert.Contains("LaunchFolderSelect:", disabledText);
                Assert.Contains("OtherKey:value", disabledText);
                Assert.Contains("SystemSystemUpdate:3", disabledText);
                Assert.True(File.Exists(disable.BackupPath));

                var enable = AutoUpdateSettings.Enable(exe, null);

                Assert.True(enable.Changed);
                Assert.Equal(original, File.ReadAllText(setup));
                Assert.False(File.Exists(setup + ".pclfix-update.json"));
            }
        }

        [Fact]
        public void Enable_RemovesSettingWhenItWasNotPresentBefore()
        {
            using (var workspace = new TempWorkspace())
            {
                var exe = CreatePcl(workspace);
                var setup = AutoUpdateSettings.GetSetupIniPath(exe);
                Directory.CreateDirectory(Path.GetDirectoryName(setup));
                File.WriteAllText(setup, "A:1\r\nB:2", new UTF8Encoding(false));

                AutoUpdateSettings.Disable(exe, true, null);
                AutoUpdateSettings.Enable(exe, null);

                var text = File.ReadAllText(setup);
                Assert.DoesNotContain("SystemSystemUpdate", text);
                Assert.Contains("A:1", text);
                Assert.Contains("B:2", text);
            }
        }

        [Fact]
        public void Disable_IsIdempotent()
        {
            using (var workspace = new TempWorkspace())
            {
                var exe = CreatePcl(workspace);
                AutoUpdateSettings.Disable(exe, true, null);
                var second = AutoUpdateSettings.Disable(exe, true, null);

                Assert.True(second.AlreadyInState);
                Assert.False(second.Changed);
                Assert.True(AutoUpdateSettings.Read(exe).Disabled);
            }
        }

        [Fact]
        public void MalformedValue_IsRecognisedAndReplaced()
        {
            using (var workspace = new TempWorkspace())
            {
                var exe = CreatePcl(workspace);
                var setup = AutoUpdateSettings.GetSetupIniPath(exe);
                Directory.CreateDirectory(Path.GetDirectoryName(setup));
                File.WriteAllText(setup, "SystemSystemUpdate:not-a-number\r\n", new UTF8Encoding(false));

                var before = AutoUpdateSettings.Read(exe);
                Assert.Equal(AutoUpdateState.Unknown, before.State);

                AutoUpdateSettings.Disable(exe, true, null);
                Assert.True(AutoUpdateSettings.Read(exe).Disabled);
            }
        }

        [Fact]
        public void Utf8Bom_IsPreserved()
        {
            using (var workspace = new TempWorkspace())
            {
                var exe = CreatePcl(workspace);
                var setup = AutoUpdateSettings.GetSetupIniPath(exe);
                Directory.CreateDirectory(Path.GetDirectoryName(setup));
                File.WriteAllText(setup, "SystemSystemUpdate:1\r\n", new UTF8Encoding(true));

                AutoUpdateSettings.Disable(exe, true, null);

                var bytes = File.ReadAllBytes(setup);
                Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
                Assert.True(AutoUpdateSettings.Read(exe).Disabled);
            }
        }

        private static string CreatePcl(TempWorkspace workspace)
        {
            var exe = workspace.Path("Plain Craft Launcher 2.exe");
            File.WriteAllText(exe, "dummy");
            return exe;
        }
    }
}
