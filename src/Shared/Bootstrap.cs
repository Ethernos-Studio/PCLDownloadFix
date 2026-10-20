using System;
using System.IO;
using System.Reflection;

namespace PCLDownloadFix
{
    /// <summary>
    /// Installs an assembly resolver for Mono.Cecil, which is embedded in both executables.
    /// This must run before any type that references Mono.Cecil is JIT-compiled.
    /// </summary>
    public static class Bootstrap
    {
        private static bool _initialized;
        private static readonly object Sync = new object();

        public static void Init()
        {
            lock (Sync)
            {
                if (_initialized) return;
                _initialized = true;
                AppDomain.CurrentDomain.AssemblyResolve += ResolveCecil;
            }
        }

        private static Assembly ResolveCecil(object sender, ResolveEventArgs args)
        {
            var requested = new AssemblyName(args.Name).Name;
            if (!string.Equals(requested, "Mono.Cecil", StringComparison.OrdinalIgnoreCase))
                return null;

            var assembly = typeof(Bootstrap).Assembly;
            using (var stream = assembly.GetManifestResourceStream("PCLDownloadFix.Mono.Cecil.dll"))
            {
                if (stream == null) return null;
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return Assembly.Load(buffer.ToArray());
                }
            }
        }
    }
}
