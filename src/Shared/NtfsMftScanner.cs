using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PCLDownloadFix
{
    public sealed class NtfsMftProgress
    {
        public string Volume { get; set; }
        public long Records { get; set; }
        public int Matches { get; set; }
    }

    public sealed class NtfsMftResult
    {
        public IList<string> Paths { get; set; }
        public string Error { get; set; }
        public int VolumesScanned { get; set; }
    }

    /// <summary>
    /// WizTree-style NTFS MFT enumeration. It reads the master file table through
    /// FSCTL_ENUM_USN_DATA instead of walking directories, so a whole volume can be
    /// searched in seconds. Raw volume access requires an elevated process.
    /// </summary>
    public static class NtfsMftScanner
    {
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FsctlEnumUsnData = 0x000900B3;
        private const int ErrorHandleEof = 38;
        private const int ErrorMoreData = 234;
        private const int OutputBufferSize = 4 * 1024 * 1024;

        public static NtfsMftResult Scan(IEnumerable<string> scopeRoots, CancellationToken token,
            Action<NtfsMftProgress> progress)
        {
            var result = new NtfsMftResult { Paths = new List<string>() };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scopes = WindowsSearchScanner.NormalizeScopes(scopeRoots);

            foreach (var drive in GetNtfsFixedDrives())
            {
                token.ThrowIfCancellationRequested();
                var volumeRoot = drive.RootDirectory.FullName;
                if (scopes.Count > 0 &&
                    !scopes.Any(scope => WindowsSearchScanner.IsInScope(scope, new[] { volumeRoot })))
                    continue;

                try
                {
                    var volumePath = @"\\.\" + drive.Name.TrimEnd('\\');
                    var paths = ScanVolume(volumePath, volumeRoot, scopes, token, progress, result);
                    result.VolumesScanned++;

                    foreach (var path in paths)
                    {
                        if (seen.Add(path)) result.Paths.Add(path);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrEmpty(result.Error))
                        result.Error = "扫描 " + volumeRoot + " 失败：" + ex.Message;
                }
            }

            if (result.VolumesScanned == 0 && string.IsNullOrEmpty(result.Error) && scopes.Count > 0)
                result.Error = "所选扫描范围中没有可读取的 NTFS 分区。";

            result.Paths = result.Paths
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return result;
        }

        private static IEnumerable<DriveInfo> GetNtfsFixedDrives()
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                bool ntfs;
                try
                {
                    ntfs = drive.IsReady &&
                           (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable) &&
                           string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    ntfs = false;
                }
                if (ntfs) yield return drive;
            }
        }

        private static IList<string> ScanVolume(string volumePath, string volumeRoot, IList<string> scopes,
            CancellationToken token, Action<NtfsMftProgress> progress, NtfsMftResult result)
        {
            using (var handle = CreateFile(volumePath, GenericRead,
                FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 5)
                        throw new UnauthorizedAccessException("读取 NTFS MFT 需要管理员权限。请以管理员身份运行本工具，或改用 Windows 搜索索引。");
                    throw new Win32Exception(error, "无法打开 NTFS 卷 " + volumeRoot + "。");
                }

                var directories = new Dictionary<ulong, DirectoryEntry>();
                var candidates = new List<Candidate>();
                var input = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MftEnumData)));
                var output = Marshal.AllocHGlobal(OutputBufferSize);
                try
                {
                    var enumData = new MftEnumData
                    {
                        StartFileReferenceNumber = 0,
                        LowUsn = 0,
                        HighUsn = long.MaxValue
                    };

                    long records = 0;
                    ulong lastFileReference = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        Marshal.StructureToPtr(enumData, input, false);

                        int bytesReturned;
                        var ok = DeviceIoControl(handle, FsctlEnumUsnData, input,
                            Marshal.SizeOf(typeof(MftEnumData)), output, OutputBufferSize,
                            out bytesReturned, IntPtr.Zero);
                        var moreData = false;

                        if (!ok)
                        {
                            var error = Marshal.GetLastWin32Error();
                            if (error == ErrorHandleEof) break;
                            if (error == ErrorMoreData && bytesReturned >= 8)
                            {
                                // The batch filled the output buffer. Continue from the
                                // last record we actually parsed instead of trusting the
                                // 8-byte header of a partial buffer.
                                moreData = true;
                            }
                            else
                            {
                                throw new Win32Exception(error,
                                    "读取 " + volumeRoot + " 的 MFT 失败（Win32 错误 " + error + "）。");
                            }
                        }

                        if (bytesReturned < 8) break;
                        var next = (ulong)Marshal.ReadInt64(output);
                        var offset = 8;
                        var parsed = 0;

                        while (offset + 60 <= bytesReturned)
                        {
                            var recordLength = Marshal.ReadInt32(output, offset);
                            if (recordLength < 60 || offset + recordLength > bytesReturned) break;

                            var major = (ushort)Marshal.ReadInt16(output, offset + 4);
                            if (major != 2)
                            {
                                offset += recordLength;
                                continue;
                            }

                            var fileReference = (ulong)Marshal.ReadInt64(output, offset + 8);
                            lastFileReference = fileReference;
                            var parentReference = (ulong)Marshal.ReadInt64(output, offset + 16);
                            var attributes = (FileAttributes)Marshal.ReadInt32(output, offset + 52);
                            var nameLength = Marshal.ReadInt16(output, offset + 56);
                            var nameOffset = Marshal.ReadInt16(output, offset + 58);
                            if (nameLength <= 0 || nameOffset < 60 || offset + nameOffset + nameLength > bytesReturned)
                            {
                                offset += recordLength;
                                continue;
                            }

                            var name = Marshal.PtrToStringUni(IntPtr.Add(output, offset + nameOffset), nameLength / 2);
                            if (string.IsNullOrEmpty(name))
                            {
                                offset += recordLength;
                                continue;
                            }

                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (fileReference != 5)
                                    directories[fileReference] = new DirectoryEntry(name, parentReference);
                            }
                            else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                // Do not trust the file name. MFT provides the complete
                                // list of EXEs; identity is checked from version info
                                // after the paths are reconstructed.
                                candidates.Add(new Candidate { Name = name, ParentReference = parentReference });
                            }

                            offset += recordLength;
                            parsed++;
                            records++;
                        }

                        if (records % 10000 < parsed && progress != null)
                        {
                            progress(new NtfsMftProgress
                            {
                                Volume = volumeRoot,
                                Records = records,
                                Matches = candidates.Count
                            });
                        }

                        if (moreData)
                        {
                            if (parsed == 0)
                                throw new Win32Exception(ErrorMoreData,
                                    "读取 " + volumeRoot + " 的 MFT 时收到不完整的数据块。");
                            if (lastFileReference == ulong.MaxValue) break;
                            next = lastFileReference + 1;
                        }
                        if (next == 0 || parsed == 0) break;
                        enumData.StartFileReferenceNumber = next;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(input);
                    Marshal.FreeHGlobal(output);
                }

                var results = new List<string>();
                foreach (var candidate in candidates)
                {
                    var path = BuildPath(candidate, directories, volumeRoot);
                    if (path == null || !File.Exists(path)) continue;
                    if (!WindowsSearchScanner.IsInScope(path, scopes)) continue;
                    if (!PclIdentity.IsPclExecutable(path)) continue;
                    results.Add(path);
                }
                return results;
            }
        }

        private static string BuildPath(Candidate candidate, Dictionary<ulong, DirectoryEntry> directories,
            string volumeRoot)
        {
            var parts = new List<string>();
            var current = candidate.ParentReference;
            var guard = 0;

            while (current != 0 && current != 5 && guard++ < 512)
            {
                DirectoryEntry entry;
                if (!directories.TryGetValue(current, out entry)) return null;
                parts.Add(entry.Name);
                if (entry.ParentReference == current) break;
                current = entry.ParentReference;
            }

            parts.Reverse();
            var root = volumeRoot;
            if (!root.EndsWith("\\", StringComparison.Ordinal)) root += "\\";
            return root + string.Join("\\", parts) + (parts.Count > 0 ? "\\" : "") + candidate.Name;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct MftEnumData
        {
            public ulong StartFileReferenceNumber;
            public long LowUsn;
            public long HighUsn;
        }

        private sealed class DirectoryEntry
        {
            public DirectoryEntry(string name, ulong parentReference)
            {
                Name = name;
                ParentReference = parentReference;
            }

            public string Name { get; private set; }
            public ulong ParentReference { get; private set; }
        }

        private sealed class Candidate
        {
            public string Name;
            public ulong ParentReference;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
            IntPtr inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize,
            out int bytesReturned, IntPtr overlapped);
    }
}
