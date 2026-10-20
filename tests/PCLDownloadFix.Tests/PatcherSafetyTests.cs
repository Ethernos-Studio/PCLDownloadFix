using Xunit;

namespace PCLDownloadFix.Tests
{
    public class PatcherSafetyTests
    {
        [Fact]
        public void PartiallyPatchedFileWithoutBackup_IsRefused()
        {
            using (var workspace = new TempWorkspace())
            {
                var path = SyntheticPcl.Write(workspace.Path("PCL.exe"), false,
                    modpackMarker: false, resourceMarker: false);

                var patcher = new Patcher(_ => { });
                var exception = Assert.Throws<PatchException>(() => patcher.Apply(path, new PatchOptions()));

                Assert.Contains("部分修复", exception.Message);
                Assert.Contains("原版备份", exception.Message);
            }
        }

        [Fact]
        public void InvalidBackup_IsNotUsedForRestore()
        {
            using (var workspace = new TempWorkspace())
            {
                var path = SyntheticPcl.Write(workspace.Path("PCL.exe"), false,
                    modpackMarker: false, resourceMarker: false);
                var backup = BackupStore.LegacyPath(path);
                System.IO.File.WriteAllText(backup, "not a PCL backup");

                var patcher = new Patcher(_ => { });
                var info = patcher.Inspect(path);

                Assert.True(info.BackupExists);
                Assert.False(info.BackupIsValidOriginal);
                Assert.Throws<PatchException>(() => patcher.Restore(path, false));
            }
        }
    }
}
