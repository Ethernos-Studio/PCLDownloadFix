using System.Text;
using Mono.Cecil;
using Xunit;

namespace PCLDownloadFix.Tests
{
    public class AssemblyFingerprintTests
    {
        [Fact]
        public void EnsureSame_RejectsAddedEmbeddedResource()
        {
            using (var workspace = new TempWorkspace())
            {
                var path = SyntheticPcl.Write(workspace.Path("pcl.dll"), false);
                using (var assembly = AssemblyDefinition.ReadAssembly(path))
                {
                    var before = AssemblyFingerprint.Capture(assembly);
                    assembly.MainModule.Resources.Add(new EmbeddedResource(
                        "new.resource", ManifestResourceAttributes.Public, Encoding.UTF8.GetBytes("payload")));
                    var after = AssemblyFingerprint.Capture(assembly);

                    Assert.Throws<PatchException>(() => before.EnsureSame(after, "test"));
                }
            }
        }

        [Fact]
        public void EnsureSame_AcceptsUnchangedAssembly()
        {
            using (var workspace = new TempWorkspace())
            {
                var path = SyntheticPcl.Write(workspace.Path("pcl.dll"), false);
                using (var assembly = AssemblyDefinition.ReadAssembly(path))
                {
                    var before = AssemblyFingerprint.Capture(assembly);
                    var after = AssemblyFingerprint.Capture(assembly);
                    before.EnsureSame(after, "test");
                }
            }
        }
    }
}
