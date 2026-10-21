using System;
using System.IO;
using System.Text;

namespace PCLDownloadFix.Gui
{
    /// <summary>
    /// Per-run diagnostic log for batch operations. It records the scan mode, scope,
    /// MFT volume/cursor errors and per-instance results so a failed "MFT -> directory
    /// fallback" can be diagnosed after the fact.
    /// </summary>
    internal sealed class BatchLog : IDisposable
    {
        private readonly object _sync = new object();
        private StreamWriter _writer;

        public BatchLog()
        {
            try
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PCLDownloadFix", "Logs");
                Directory.CreateDirectory(folder);
                FilePath = Path.Combine(folder,
                    "batch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                    Guid.NewGuid().ToString("N").Substring(0, 4) + ".log");
                _writer = new StreamWriter(FilePath, false, new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
            }
            catch
            {
                _writer = null;
                FilePath = null;
            }
        }

        public string FilePath { get; private set; }

        public void Write(string message)
        {
            if (_writer == null) return;
            lock (_sync)
            {
                try
                {
                    _writer.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message);
                }
                catch
                {
                }
            }
        }

        public void Write(string message, Exception exception)
        {
            Write(message + Environment.NewLine + (exception == null ? "" : exception.ToString()));
        }

        public void Dispose()
        {
            lock (_sync)
            {
                try { if (_writer != null) _writer.Dispose(); } catch { }
                _writer = null;
            }
        }
    }
}
