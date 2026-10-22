using Xunit;

namespace PCLDownloadFix.Tests
{
    public class FclPatcherTests
    {
        [Fact]
        public void PackageToken_ReplacesFclButNotFclcore()
        {
            var text = "com.tungsten.fcl.provider " +
                       "com.tungsten.fclcore.mod " +
                       "com.tungsten.fcllibrary.crash";

            var result = FclPatcher.ReplacePackageToken(text);

            Assert.Contains("com.tungsten.fcl.fix.provider", result);
            Assert.Contains("com.tungsten.fclcore.mod", result);
            Assert.Contains("com.tungsten.fcllibrary.crash", result);
        }

        [Fact]
        public void PackageToken_ReplacesStandalonePackage()
        {
            Assert.Equal("com.tungsten.fcl.fix",
                FclPatcher.ReplacePackageToken("com.tungsten.fcl"));
        }

        [Fact]
        public void ReplaceMethod_RewritesRegionGate()
        {
            var smali =
                ".method public static isChinaMainlandNetwork()Z\r\n" +
                "    .registers 3\r\n" +
                "    const/4 v0, 0x1\r\n" +
                "    return v0\r\n" +
                ".end method\r\n";

            var patched = FclPatcher.ReplaceMethod(smali, "isChinaMainlandNetwork", "()Z",
                ".registers 1\r\n    const/4 v0, 0x0\r\n    return v0");

            Assert.Contains("const/4 v0, 0x0", patched);
            Assert.Contains("return v0", patched);
            Assert.DoesNotContain("0x1", patched);
        }
    }
}
