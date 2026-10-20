using System.IO;
using Xunit;

namespace PCLDownloadFix.Tests
{
    public class FileOpsTests
    {
        [Fact]
        public void Replace_ReplacesContentAndRestoresReadOnlyAttribute()
        {
            using (var workspace = new TempWorkspace())
            {
                var source = workspace.Path("source.tmp");
                var destination = workspace.Path("destination.exe");
                File.WriteAllText(source, "new");
                File.WriteAllText(destination, "old");
                File.SetAttributes(destination, FileAttributes.ReadOnly);

                FileOps.Replace(source, destination);

                Assert.Equal("new", File.ReadAllText(destination));
                Assert.True((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0);
                Assert.False(File.Exists(source));
            }
        }

        [Fact]
        public void Replace_MovesWhenDestinationDoesNotExist()
        {
            using (var workspace = new TempWorkspace())
            {
                var source = workspace.Path("source.tmp");
                var destination = workspace.Path("new.exe");
                File.WriteAllText(source, "payload");

                FileOps.Replace(source, destination);

                Assert.Equal("payload", File.ReadAllText(destination));
                Assert.False(File.Exists(source));
            }
        }
    }
}
