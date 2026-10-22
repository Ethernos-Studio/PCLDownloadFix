using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace PCLDownloadFix.Tests
{
    public class HmclPatcherTests
    {
        [Fact]
        public void PatchJar_ReplacesRegionClass_AndRestoreRecoversIt()
        {
            using (var workspace = new TempWorkspace())
            {
                var jar = Path.Combine(workspace.Root, "HMCL.jar");
                CreateJar(jar, Encoding.UTF8.GetBytes("original-region-class"));

                var before = HmclPatcher.Inspect(jar);
                Assert.True(before.HasRegionGate);
                Assert.False(before.IsPatched);

                var patch = HmclPatcher.Patch(jar);
                Assert.True(patch.Success);
                Assert.True(patch.Changed);
                Assert.True(File.Exists(patch.BackupPath));

                var after = HmclPatcher.Inspect(jar);
                Assert.True(after.IsPatched);
                Assert.True(after.HasRegionGate);

                var restore = HmclPatcher.Restore(jar);
                Assert.True(restore.Success);
                Assert.False(HmclPatcher.Inspect(jar).IsPatched);
            }
        }

        [Fact]
        public void PatchExe_ReplacesOnlyAppendedJar()
        {
            using (var workspace = new TempWorkspace())
            {
                var exe = Path.Combine(workspace.Root, "HMCL.exe");
                var prefix = new byte[] { (byte)'M', (byte)'Z', 0, 0, 1, 2, 3, 4 };
                var jar = Path.Combine(workspace.Root, "inner.jar");
                CreateJar(jar, Encoding.UTF8.GetBytes("original-region-class"));

                using (var output = File.Create(exe))
                {
                    output.Write(prefix, 0, prefix.Length);
                    using (var input = File.OpenRead(jar))
                        input.CopyTo(output);
                }

                var result = HmclPatcher.Patch(exe);
                Assert.True(result.Success);

                var bytes = File.ReadAllBytes(exe);
                for (var i = 0; i < prefix.Length; i++)
                    Assert.Equal(prefix[i], bytes[i]);
                Assert.True(HmclPatcher.Inspect(exe).IsPatched);
            }
        }

        private static void CreateJar(string path, byte[] regionClass)
        {
            using (var stream = File.Create(path))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var manifest = archive.CreateEntry("META-INF/MANIFEST.MF");
                using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
                {
                    writer.WriteLine("Manifest-Version: 1.0");
                    writer.WriteLine("Implementation-Version: 3.16.4-test");
                }

                var region = archive.CreateEntry(HmclPatcher.RegionClassEntry);
                using (var output = region.Open())
                    output.Write(regionClass, 0, regionClass.Length);
            }
        }
    }
}
