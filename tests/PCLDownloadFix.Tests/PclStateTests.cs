using Xunit;

namespace PCLDownloadFix.Tests
{
    public class PclStateTests
    {
        [Fact]
        public void GuardedBuild_IsOriginal()
        {
            using (var workspace = new TempWorkspace())
            {
                var path = SyntheticPcl.Write(workspace.Path("pcl.dll"), true,
                    loaderMarker: false, modpackMarker: false, resourceMarker: false);
                using (var target = PclTarget.Load(path))
                {
                    string detail;
                    var state = target.GetState(out detail);
                    Assert.Equal(PatchState.Original, state);
                    Assert.Contains("限制", detail);
                }
            }
        }

        [Fact]
        public void IntactBuild_IsUnrestricted()
        {
            using (var workspace = new TempWorkspace())
            {
                var path = SyntheticPcl.Write(workspace.Path("pcl.dll"), false);
                using (var target = PclTarget.Load(path))
                {
                    string detail;
                    var state = target.GetState(out detail);
                    Assert.Equal(PatchState.Unrestricted, state);
                }
            }
        }

        [Fact]
        public void PartialBuild_IsPartial()
        {
            using (var workspace = new TempWorkspace())
            {
                var path = SyntheticPcl.Write(workspace.Path("pcl.dll"), false,
                    modpackMarker: false, resourceMarker: false);
                using (var target = PclTarget.Load(path))
                {
                    string detail;
                    var state = target.GetState(out detail);
                    Assert.Equal(PatchState.Partial, state);
                }
            }
        }

        [Fact]
        public void MissingMethods_IsUnsupported()
        {
            using (var workspace = new TempWorkspace())
            {
                var path = SyntheticPcl.Write(workspace.Path("pcl.dll"), true, omitMethods: true);
                using (var target = PclTarget.Load(path))
                {
                    string detail;
                    var state = target.GetState(out detail);
                    Assert.Equal(PatchState.Unsupported, state);
                }
            }
        }
    }
}
