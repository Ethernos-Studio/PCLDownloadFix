using Mono.Cecil;
using Xunit;

namespace PCLDownloadFix.Tests
{
    public class DonorTests
    {
        [Fact]
        public void EmbeddedDonor_MatchesPinnedHashAndShape()
        {
            using (var stream = EmbeddedResources.OpenDonor())
            using (var donor = AssemblyDefinition.ReadAssembly(stream))
            {
                var type = donor.MainModule.GetType("PCLDownloadRestore.RestoreMethods");
                Assert.NotNull(type);
                Assert.Equal(RestoreSpecs.All.Length, type.Methods.Count);
                Assert.All(type.Methods, method => Assert.True(method.IsStatic));
            }
        }

        [Fact]
        public void EmbeddedCecil_MatchesPinnedHash()
        {
            var cecil = EmbeddedResources.Read(EmbeddedResources.CecilName);
            Assert.NotNull(cecil);
            Assert.Equal(EmbeddedResources.CecilSha256, HashUtil.Sha256Bytes(cecil));
        }
    }
}
