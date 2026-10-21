using System.IO;
using Xunit;

namespace PCLDownloadFix.Tests
{
    public class ScanScopeTests
    {
        [Fact]
        public void DriveScope_MatchesOnlyThatDrive()
        {
            var scopes = WindowsSearchScanner.NormalizeScopes(new[] { "E:\\" });

            Assert.True(WindowsSearchScanner.IsInScope("E:\\PCL\\Plain Craft Launcher 2.exe", scopes));
            Assert.False(WindowsSearchScanner.IsInScope("C:\\PCL\\Plain Craft Launcher 2.exe", scopes));
        }

        [Fact]
        public void FolderScope_MatchesNestedFilesOnly()
        {
            var scopes = WindowsSearchScanner.NormalizeScopes(new[] { @"E:\Desktop\PCL" });

            Assert.True(WindowsSearchScanner.IsInScope(@"E:\Desktop\PCL\样本.exe", scopes));
            Assert.False(WindowsSearchScanner.IsInScope(@"E:\Desktop\Other\样本.exe", scopes));
        }

        [Fact]
        public void EmptyScope_MatchesEverything()
        {
            var scopes = WindowsSearchScanner.NormalizeScopes(null);
            Assert.True(WindowsSearchScanner.IsInScope(@"Z:\anything.exe", scopes));
        }

        [Fact]
        public void ScopeNormalization_TrimsTrailingSlash()
        {
            var scopes = WindowsSearchScanner.NormalizeScopes(new[] { @"E:\Desktop\" });
            Assert.True(WindowsSearchScanner.IsInScope(@"E:\Desktop\PCL.exe", scopes));
        }
    }
}
