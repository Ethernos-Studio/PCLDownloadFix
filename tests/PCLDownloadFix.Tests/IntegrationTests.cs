using System;
using System.IO;
using Xunit;

namespace PCLDownloadFix.Tests
{
    /// <summary>
    /// These tests need real PCL binaries and are skipped when the environment variables
    /// are not set. They are the only tests that exercise the donor IL end to end.
    ///
    ///   PCLDF_TEST_ORIGINAL_414  PCL 2.99 snapshot 414 (restricted)
    ///   PCLDF_TEST_ORIGINAL_413  PCL 2.99 release 413 (restricted)
    ///   PCLDF_TEST_PRE_412       PCL 2.14 release 412 (pre-restriction, donor source)
    /// </summary>
    public class IntegrationTests
    {
        private static string Env(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim('"'));
        }

        [Fact]
        [Trait("Category", "Integration")]
        public void BuildInfo_ReportsInternalBuildKeys()
        {
            var source414 = Env("PCLDF_TEST_ORIGINAL_414");
            var source413 = Env("PCLDF_TEST_ORIGINAL_413");
            var source412 = Env("PCLDF_TEST_PRE_412");
            if (source414 == null || source413 == null || source412 == null) return;

            Assert.Equal("414.00", PclBuildInfo.Probe(source414).BuildKey);
            Assert.Equal("413.50", PclBuildInfo.Probe(source413).BuildKey);
            Assert.Equal("411.50", PclBuildInfo.Probe(source412).BuildKey);

            var patcher = new Patcher(_ => { });
            Assert.Equal(414, patcher.Inspect(source414).VersionCode);
            Assert.Equal(413, patcher.Inspect(source413).VersionCode);
            Assert.Equal(411, patcher.Inspect(source412).VersionCode);
        }

        [Fact]
        [Trait("Category", "Integration")]
        public void Patch414_Restore_RoundTrips()
        {
            var source = Env("PCLDF_TEST_ORIGINAL_414");
            if (source == null) return;

            using (var workspace = new TempWorkspace())
            {
                var target = workspace.Path("PCL.exe");
                File.Copy(source, target);
                var originalHash = HashUtil.Sha256File(target);

                var patcher = new Patcher(_ => { });
                var result = patcher.Apply(target, new PatchOptions());

                Assert.True(result.Success);
                Assert.False(result.DryRun);
                Assert.True(File.Exists(result.BackupPath));
                Assert.Equal(originalHash, HashUtil.Sha256File(result.BackupPath));
                Assert.Equal(PatchState.Patched, patcher.Inspect(target).State);

                var restore = patcher.Restore(target, false);

                Assert.True(restore.Success);
                Assert.Equal(originalHash, HashUtil.Sha256File(target));
                Assert.Equal(PatchState.Original, patcher.Inspect(target).State);
            }
        }

        [Fact]
        [Trait("Category", "Integration")]
        public void Patch413_AddsMissingCallbackAndRestores()
        {
            var source = Env("PCLDF_TEST_ORIGINAL_413");
            if (source == null) return;

            using (var workspace = new TempWorkspace())
            {
                var target = workspace.Path("PCL.exe");
                File.Copy(source, target);
                var originalHash = HashUtil.Sha256File(target);

                var patcher = new Patcher(_ => { });
                var result = patcher.Apply(target, new PatchOptions());

                Assert.True(result.Success);
                Assert.Equal(PatchState.Patched, patcher.Inspect(target).State);

                patcher.Restore(target, false);
                Assert.Equal(originalHash, HashUtil.Sha256File(target));
            }
        }

        [Fact]
        [Trait("Category", "Integration")]
        public void PatchAfterPclUpdate_ArchivesOldBackupAndRestoresNewBuild()
        {
            var source414 = Env("PCLDF_TEST_ORIGINAL_414");
            var source413 = Env("PCLDF_TEST_ORIGINAL_413");
            if (source414 == null || source413 == null) return;

            using (var workspace = new TempWorkspace())
            {
                var target = workspace.Path("PCL.exe");
                File.Copy(source414, target);
                var hash414 = HashUtil.Sha256File(target);

                var patcher = new Patcher(_ => { });
                Assert.True(patcher.Apply(target, new PatchOptions()).Success);

                // Simulate PCL updating itself in place.
                File.Copy(source413, target, true);
                var hash413 = HashUtil.Sha256File(target);

                var second = patcher.Apply(target, new PatchOptions());

                Assert.True(second.Success);
                Assert.True(File.Exists(BackupStore.ArchivedPath(target, hash414)));
                Assert.Equal(hash414, HashUtil.Sha256File(BackupStore.ArchivedPath(target, hash414)));
                Assert.Equal(hash413, HashUtil.Sha256File(BackupStore.LegacyPath(target)));

                patcher.Restore(target, false);
                Assert.Equal(hash413, HashUtil.Sha256File(target));
            }
        }

        [Fact]
        [Trait("Category", "Integration")]
        public void PatchWithoutBackup_DoesNotReuseOldBackup()
        {
            var source414 = Env("PCLDF_TEST_ORIGINAL_414");
            var source413 = Env("PCLDF_TEST_ORIGINAL_413");
            if (source414 == null || source413 == null) return;

            using (var workspace = new TempWorkspace())
            {
                var target = workspace.Path("PCL.exe");
                File.Copy(source414, target);
                var patcher = new Patcher(_ => { });
                Assert.True(patcher.Apply(target, new PatchOptions()).Success);

                // A PCL update happens, then the user explicitly chooses not to create a
                // new backup. The stale 414 backup must not be advertised as restorable.
                File.Copy(source413, target, true);
                var result = patcher.Apply(target, new PatchOptions { CreateBackup = false });

                Assert.True(result.Success);
                Assert.Null(result.BackupPath);

                var info = patcher.Inspect(target);
                Assert.Equal(PatchState.Unrestricted, info.State);
                Assert.True(info.NoFixNeeded);
                Assert.False(info.BackupExists);
            }
        }

        [Fact]
        [Trait("Category", "Integration")]
        public void PreRestrictionBuild_CanDisableAndRestoreAutoUpdate()
        {
            var source = Env("PCLDF_TEST_PRE_412");
            if (source == null) return;

            using (var workspace = new TempWorkspace())
            {
                var target = workspace.Path("PCL.exe");
                File.Copy(source, target);

                var patcher = new Patcher(_ => { });
                var disabled = patcher.DisableAutoUpdate(target);

                Assert.True(disabled.Success);
                Assert.True(disabled.Changed);
                var info = patcher.Inspect(target);
                Assert.True(info.IsPreRestriction);
                Assert.True(info.AutoUpdateDisabled);
                Assert.Equal(3, info.AutoUpdateValue);
                Assert.Contains("SystemSystemUpdate:3", File.ReadAllText(info.SetupIniPath));

                var enabled = patcher.EnableAutoUpdate(target);

                Assert.True(enabled.Success);
                Assert.True(enabled.Changed);
                Assert.False(patcher.Inspect(target).AutoUpdateDisabled);
            }
        }

        [Fact]
        [Trait("Category", "Integration")]
        public void PreRestrictionBuild_NeedsNoPatch()
        {
            var source = Env("PCLDF_TEST_PRE_412");
            if (source == null) return;

            using (var workspace = new TempWorkspace())
            {
                var target = workspace.Path("PCL.exe");
                File.Copy(source, target);
                var originalHash = HashUtil.Sha256File(target);

                var patcher = new Patcher(_ => { });
                var info = patcher.Inspect(target);

                Assert.Equal(PatchState.Unrestricted, info.State);
                Assert.True(info.NoFixNeeded);

                var result = patcher.Apply(target, new PatchOptions());
                Assert.True(result.AlreadyPatched);
                Assert.Equal(originalHash, HashUtil.Sha256File(target));
            }
        }
    }
}
