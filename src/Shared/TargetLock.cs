using System;
using System.IO;
using System.Text;
using System.Threading;

namespace PCLDownloadFix
{
    /// <summary>
    /// Serialises write operations on a single PCL executable. Read-only inspection does
    /// not take the lock; the final hash check still protects against a concurrent write.
    /// </summary>
    public sealed class TargetLock : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _held;

        private TargetLock(Mutex mutex)
        {
            _mutex = mutex;
            _held = true;
        }

        public static TargetLock Acquire(string targetPath, TimeSpan timeout)
        {
            // Windows paths are case-insensitive; normalise so differently-cased
            // invocations still contend on the same lock.
            var full = Path.GetFullPath(targetPath).ToUpperInvariant();
            var digest = HashUtil.Sha256Bytes(Encoding.UTF8.GetBytes(full));
            var name = "Local\\PCLDownloadFix-" + digest.Substring(0, 16);

            var mutex = new Mutex(false, name);
            var acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner crashed while holding the lock; the file state is
                    // re-checked by the caller before touching it, so taking over is safe.
                    acquired = true;
                }
            }
            finally
            {
                if (!acquired) mutex.Dispose();
            }

            if (!acquired)
                throw new PatchException("另一个 PCL 下载修复器实例正在操作同一文件，请稍后重试。");
            return new TargetLock(mutex);
        }

        public void Dispose()
        {
            if (_held)
            {
                try { _mutex.ReleaseMutex(); }
                catch (ApplicationException) { }
                _held = false;
            }
            _mutex.Dispose();
        }
    }
}
