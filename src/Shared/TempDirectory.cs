using System;
using System.IO;

namespace PCLDownloadFix
{
    public sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; private set; }

        public static TempDirectory Create(string purpose)
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCLDownloadFix");
            Directory.CreateDirectory(root);
            var path = System.IO.Path.Combine(root, purpose + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, true);
            }
            catch
            {
                // Temporary files are harmless; ignore cleanup failures.
            }
        }
    }
}
