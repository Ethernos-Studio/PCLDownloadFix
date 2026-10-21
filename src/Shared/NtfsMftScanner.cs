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
        public IList<string> FailedVolumes { get; set; }
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
        private const uint FsctlQueryUsnJournal = 0x000900F4;
        private const int ErrorHandleEof = 38;
        private const int ErrorInvalidParameter = 87;
        private const int ErrorMoreData = 234;
        private const int ErrorFileCorrupt = 1392;
        private const int OutputBufferSize = 8 * 1024 * 1024;
        // USN_RECORD file references contain a 16-bit sequence number in the high
        // bits. The MFT record index is the low 48 bits; parent lookup and the root
        // check must use the index, not the full reference.
        private const ulong FileReferenceMask = 0x0000FFFFFFFFFFFF;

        public static NtfsMftResult Scan(IEnumerable<string> scopeRoots, CancellationToken token,
            Action<NtfsMftProgress> progress, Action<string> log = null)
        {
            var result = new NtfsMftResult
            {
                Paths = new List<string>(),
                FailedVolumes = new List<string>()
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scopes = WindowsSearchScanner.NormalizeScopes(scopeRoots);
            WriteLog(log, "MFT scan start; scopes=" +
                (scopes.Count == 0 ? "(all NTFS volumes)" : string.Join("; ", scopes)));

            foreach (var drive in GetNtfsFixedDrives())
            {
                token.ThrowIfCancellationRequested();
                var volumeRoot = drive.RootDirectory.FullName;
                if (scopes.Count > 0 &&
                    !scopes.Any(scope => WindowsSearchScanner.IsInScope(scope, new[] { volumeRoot })))
                    continue;

                try
                {
                    WriteLog(log, "MFT volume start " + volumeRoot);
                    var volumePath = @"\\.\" + drive.Name.TrimEnd('\\');
                    var paths = ScanVolume(volumePath, volumeRoot, scopes, token, progress, result, log);
                    result.VolumesScanned++;

                    foreach (var path in paths)
                    {
                        if (seen.Add(path)) result.Paths.Add(path);
                    }
                    WriteLog(log, "MFT volume done " + volumeRoot + "; paths=" + paths.Count);
                }
                catch (OperationCanceledException)
                {
                    WriteLog(log, "MFT cancelled at " + volumeRoot);
                    throw;
                }
                catch (Exception ex)
                {
                    if (!result.FailedVolumes.Contains(volumeRoot, StringComparer.OrdinalIgnoreCase))
                        result.FailedVolumes.Add(volumeRoot);
                    if (string.IsNullOrEmpty(result.Error))
                        result.Error = "扫描 " + volumeRoot + " 失败：" + ex.Message;
                    WriteLog(log, "MFT volume failed " + volumeRoot, ex);
                }
            }

            if (result.VolumesScanned == 0 && string.IsNullOrEmpty(result.Error) && scopes.Count > 0)
                result.Error = "所选扫描范围中没有可读取的 NTFS 分区。";
            WriteLog(log, "MFT scan end; volumes=" + result.VolumesScanned +
                "; paths=" + result.Paths.Count + "; failed=" + result.FailedVolumes.Count);
            if (!string.IsNullOrEmpty(result.Error)) WriteLog(log, "MFT error: " + result.Error);

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
            CancellationToken token, Action<NtfsMftProgress> progress, NtfsMftResult result,
            Action<string> log)
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
                long records = 0;
                var versionCounts = new Dictionary<int, int>();
                try
                {
                    // FSCTL_ENUM_USN_DATA uses HighUsn as an upper filter. Passing
                    // long.MaxValue can make the driver walk an invalid range on some
                    // volumes (ERROR_FILE_CORRUPT / 1392); use the journal's NextUsn.
                    long nextUsn;
                    int journalError;
                    var journalOk = TryQueryJournal(handle, out nextUsn, out journalError) && nextUsn > 0;
                    if (!journalOk) nextUsn = long.MaxValue;
                    WriteLog(log, "MFT " + volumeRoot + " journal=" +
                        (journalOk
                            ? "NextUsn=0x" + nextUsn.ToString("x")
                            : "unavailable (Win32 " + journalError + "), using MaxUsn"));

                    var enumData = new MftEnumData
                    {
                        StartFileReferenceNumber = 0,
                        LowUsn = 0,
                        HighUsn = nextUsn
                    };

                    ulong lastFileReference = 0;
                    var corruptRetries = 0;
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
                            else if (error == ErrorFileCorrupt)
                            {
                                // Isolated corrupt regions can fail one batch. Skip
                                // forward from the last valid entry a few times, like
                                // WizTree, instead of abandoning the whole volume.
                                if (lastFileReference > 0 && lastFileReference < ulong.MaxValue &&
                                    corruptRetries < 3)
                                {
                                    // The failing start itself may be the corrupt record;
                                    // skip a little on the first retry and progressively
                                    // larger ranges if the damage is wider.
                                    var skip = corruptRetries == 0 ? 2UL
                                        : corruptRetries == 1 ? 1024UL : 65536UL;
                                    if (lastFileReference > ulong.MaxValue - skip)
                                        throw new IOException("卷 " + volumeRoot + " 的 MFT 游标溢出。");
                                    corruptRetries++;
                                    enumData.StartFileReferenceNumber = lastFileReference + skip;
                                    WriteLog(log, "MFT " + volumeRoot + " ERROR_FILE_CORRUPT; skip " +
                                        skip + " to 0x" + enumData.StartFileReferenceNumber.ToString("x") +
                                        "; retry " + corruptRetries + "/3");
                                    continue;
                                }
                                throw new IOException(
                                    "卷 " + volumeRoot + " 的 MFT/USN 数据损坏（Win32 1392）。" +
                                    "建议先运行 chkdsk /scan，或改用目录遍历模式。");
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
                            int versionSeen;
                            versionCounts.TryGetValue(major, out versionSeen);
                            versionCounts[major] = versionSeen + 1;
                            int parentOffset, attributesOffset, nameLengthOffset, nameOffsetField;
                            int headerSize;
                            if (major == 2)
                            {
                                parentOffset = 16;
                                attributesOffset = 52;
                                nameLengthOffset = 56;
                                nameOffsetField = 58;
                                headerSize = 60;
                            }
                            else if (major == 3 || major == 4)
                            {
                                // USN_RECORD_V3/V4 use 128-bit file IDs; the low
                                // 64 bits still contain the MFT record index.
                                parentOffset = 24;
                                attributesOffset = 68;
                                nameLengthOffset = 72;
                                nameOffsetField = 74;
                                headerSize = 76;
                            }
                            else
                            {
                                offset += recordLength;
                                continue;
                            }

                            var fileReference =
                                (ulong)Marshal.ReadInt64(output, offset + 8) & FileReferenceMask;
                            lastFileReference = fileReference;
                            var parentReference =
                                (ulong)Marshal.ReadInt64(output, offset + parentOffset) & FileReferenceMask;
                            var attributes = (FileAttributes)Marshal.ReadInt32(output, offset + attributesOffset);
                            var nameLength = Marshal.ReadInt16(output, offset + nameLengthOffset);
                            var nameOffset = Marshal.ReadInt16(output, offset + nameOffsetField);
                            if (nameLength <= 0 || nameOffset < headerSize ||
                                offset + nameOffset + nameLength > bytesReturned)
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

                        if (parsed > 0) corruptRetries = 0;
                        if (moreData)
                        {
                            if (parsed == 0)
                                throw new Win32Exception(ErrorMoreData,
                                    "读取 " + volumeRoot + " 的 MFT 时收到不完整的数据块。");
                            // Prefer the next-FRN value supplied by the driver, but never
                            // go backwards or trust a zero value from a partial buffer.
                            if (next == 0 || next <= lastFileReference)
                            {
                                if (lastFileReference == ulong.MaxValue) break;
                                next = lastFileReference + 1;
                            }
                        }
                        if (next == 0 || parsed == 0) break;
                        if (next <= enumData.StartFileReferenceNumber)
                            throw new Win32Exception(ErrorInvalidParameter,
                                "读取 " + volumeRoot + " 的 MFT 时游标没有前进，已停止以避免死循环。");
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
                WriteLog(log, "MFT " + volumeRoot + " stats; records=" + records +
                    "; versions=" + string.Join(",", versionCounts.Select(pair =>
                        pair.Key + ":" + pair.Value)) +
                    "; dirs=" + directories.Count + "; exeCandidates=" + candidates.Count +
                    "; paths=" + results.Count);
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

        internal static ulong MaskFileReference(ulong fileReference)
        {
            return fileReference & FileReferenceMask;
        }

        private static void WriteLog(Action<string> log, string message)
        {
            if (log == null) return;
            try { log(message); } catch { }
        }

        private static void WriteLog(Action<string> log, string message, Exception exception)
        {
            if (log == null) return;
            try { log(message + Environment.NewLine + exception); } catch { }
        }

        private static bool TryQueryJournal(SafeFileHandle handle, out long nextUsn, out int error)
        {
            nextUsn = 0;
            error = 0;
            var size = Marshal.SizeOf(typeof(UsnJournalData));
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                int returned;
                if (!DeviceIoControl(handle, FsctlQueryUsnJournal, IntPtr.Zero, 0,
                        buffer, size, out returned, IntPtr.Zero))
                {
                    error = Marshal.GetLastWin32Error();
                    return false;
                }
                // V0 is 56 bytes, V1 is 64; only NextUsn (offset 16..24) is required.
                if (returned < 24) return false;

                var data = (UsnJournalData)Marshal.PtrToStructure(buffer, typeof(UsnJournalData));
                nextUsn = data.NextUsn;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.HResult;
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct MftEnumData
        {
            public ulong StartFileReferenceNumber;
            public long LowUsn;
            public long HighUsn;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct UsnJournalData
        {
            public ulong UsnJournalId;
            public long FirstUsn;
            public long NextUsn;
            public long LowestValidUsn;
            public long MaxUsn;
            public ulong MaximumSize;
            public ulong AllocationDelta;
            // USN_JOURNAL_DATA_V1 on Windows 8+; the extra words make the
            // output buffer 64 bytes, otherwise FSCTL_QUERY_USN_JOURNAL fails
            // with ERROR_INSUFFICIENT_BUFFER and NextUsn is unavailable.
            public ushort MinSupportedMajorVersion;
            public ushort MaxSupportedMajorVersion;
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
