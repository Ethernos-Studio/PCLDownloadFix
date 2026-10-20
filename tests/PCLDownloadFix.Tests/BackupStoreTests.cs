using System;
using System.IO;
using Xunit;

namespace PCLDownloadFix.Tests
{
    public class BackupStoreTests
    {
        [Fact]
        public void EnsureOriginalBackup_ArchivesPreviousBuildAndCreatesNewBackup()
        {
            using (var workspace = new TempWorkspace())
            {
                var target = SyntheticPcl.Write(workspace.Path("PCL.exe"), true,
                    loaderMarker: false, modpackMarker: false, resourceMarker: false);
                var oldBackup = SyntheticPcl.Write(workspace.Path("PCL.exe.pcl-original.bak"), true,
                    loaderMarker: false, modpackMarker: false, resourceMarker: false,
                    version: new Version(2, 98, 0, 0));

                var targetHash = HashUtil.Sha256File(target);
                var oldHash = HashUtil.Sha256File(oldBackup);
                Assert.NotEqual(targetHash, oldHash);

                var active = BackupStore.EnsureOriginalBackup(target, targetHash, null);

                Assert.Equal(BackupStore.LegacyPath(target), active);
                Assert.Equal(targetHash, HashUtil.Sha256File(active));
                Assert.True(File.Exists(BackupStore.ArchivedPath(target, oldHash)));
                Assert.Equal(oldHash, HashUtil.Sha256File(BackupStore.ArchivedPath(target, oldHash)));
            }
        }

        [Fact]
        public void EnsureOriginalBackup_ReusesMatchingBackup()
        {
            using (var workspace = new TempWorkspace())
            {
                var target = SyntheticPcl.Write(workspace.Path("PCL.exe"), true,
                    loaderMarker: false, modpackMarker: false, resourceMarker: false);
                var legacy = BackupStore.LegacyPath(target);
                File.Copy(target, legacy);
                var hash = HashUtil.Sha256File(target);

                var active = BackupStore.EnsureOriginalBackup(target, hash, null);

                Assert.Equal(legacy, active);
                Assert.Empty(BackupStore.FindArchivedBackups(target));
            }
        }

        [Fact]
        public void Manifest_RoundTripsAndSelectsTheMatchingBackup()
        {
            using (var workspace = new TempWorkspace())
            {
                var target = SyntheticPcl.Write(workspace.Path("PCL.exe"), true,
                    loaderMarker: false, modpackMarker: false, resourceMarker: false);
                var hash = HashUtil.Sha256File(target);
                var archived = BackupStore.ArchivedPath(target, hash);
                File.Copy(target, archived);

                var manifest = new PatchManifest
                {
                    OriginalSha256 = hash,
                    PatchedSha256 = hash,
                    BackupFile = Path.GetFileName(archived),
                    Build = "test",
                    ToolVersion = "1.1.0",
                    CreatedUtc = DateTime.UtcNow.ToString("o")
                };

                Assert.True(BackupStore.TryWriteManifest(target, manifest));

                PatchManifest read;
                var selected = BackupStore.ResolveBackupPath(target, hash, out read);

                Assert.Equal(archived, selected);
                Assert.NotNull(read);
                Assert.Equal(Path.GetFileName(archived), read.BackupFile);
                Assert.Equal(hash, read.PatchedSha256);
            }
        }
    }
}
