using System.IO;
using System.Threading;
using Xunit;

namespace PCLDownloadFix.Tests
{
    public class PclScannerTests
    {
        [Fact]
        public void Scan_FindsPclInNestedDirectories()
        {
            using (var workspace = new TempWorkspace())
            {
                var first = Path.Combine(workspace.Root, "A", "PCL");
                var second = Path.Combine(workspace.Root, "B", "Sub");
                Directory.CreateDirectory(first);
                Directory.CreateDirectory(second);
                File.WriteAllText(Path.Combine(first, PclScanner.ExecutableName), "a");
                File.WriteAllText(Path.Combine(second, PclScanner.ExecutableName), "b");
                File.WriteAllText(Path.Combine(workspace.Root, "B", "PCL.exe"), "not the expected name");

                var results = PclScanner.Scan(new[] { workspace.Root }, 3, CancellationToken.None, null);

                Assert.Equal(2, results.Count);
                Assert.Contains(Path.Combine(first, PclScanner.ExecutableName), results);
                Assert.Contains(Path.Combine(second, PclScanner.ExecutableName), results);
            }
        }

        [Fact]
        public void Scan_RespectsDepthLimit()
        {
            using (var workspace = new TempWorkspace())
            {
                var deep = Path.Combine(workspace.Root, "1", "2", "3", "4");
                Directory.CreateDirectory(deep);
                File.WriteAllText(Path.Combine(deep, PclScanner.ExecutableName), "a");

                var results = PclScanner.Scan(new[] { workspace.Root }, 2, CancellationToken.None, null);

                Assert.Empty(results);
            }
        }

        [Fact]
        public void Scan_CanBeCancelled()
        {
            using (var workspace = new TempWorkspace())
            {
                using (var source = new CancellationTokenSource())
                {
                    source.Cancel();
                    Assert.Throws<System.OperationCanceledException>(() =>
                        PclScanner.Scan(new[] { workspace.Root }, 3, source.Token, null));
                }
            }
        }
    }
}
