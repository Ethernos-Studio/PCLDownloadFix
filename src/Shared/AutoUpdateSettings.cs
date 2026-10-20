using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace PCLDownloadFix
{
    public enum AutoUpdateState
    {
        Unknown,
        NotConfigured,
        AutoDownload,
        Notify,
        ImportantOnly,
        Disabled
    }

    public sealed class AutoUpdateInfo
    {
        public string SetupIniPath { get; set; }
        public bool SetupIniExists { get; set; }
        public bool KeyPresent { get; set; }
        public string RawValue { get; set; }
        public int? Value { get; set; }
        public AutoUpdateState State { get; set; }
        public string Error { get; set; }

        public bool Disabled
        {
            get { return State == AutoUpdateState.Disabled; }
        }

        public string Text
        {
            get
            {
                if (!string.IsNullOrEmpty(Error)) return "读取失败";
                switch (State)
                {
                    case AutoUpdateState.Disabled: return "已关闭";
                    case AutoUpdateState.AutoDownload: return "自动下载更新";
                    case AutoUpdateState.Notify: return "有新版本时提示";
                    case AutoUpdateState.ImportantOnly: return "仅重大漏洞提示";
                    case AutoUpdateState.NotConfigured: return "未配置（PCL 默认自动下载）";
                    default:
                        return string.IsNullOrEmpty(RawValue) ? "无法识别" : "无法识别（" + RawValue + "）";
                }
            }
        }
    }

    public sealed class AutoUpdateChange
    {
        public bool Success { get; set; }
        public bool Changed { get; set; }
        public bool AlreadyInState { get; set; }
        public string TargetPath { get; set; }
        public string SetupIniPath { get; set; }
        public string PreviousValue { get; set; }
        public string CurrentValue { get; set; }
        public string BackupPath { get; set; }
        public string Message { get; set; }
    }

    [DataContract]
    internal sealed class AutoUpdateBackup
    {
        [DataMember(Name = "key", Order = 0)]
        public string Key { get; set; }

        [DataMember(Name = "previousPresent", Order = 1)]
        public bool PreviousPresent { get; set; }

        [DataMember(Name = "previousValue", Order = 2)]
        public string PreviousValue { get; set; }

        [DataMember(Name = "createdUtc", Order = 3)]
        public string CreatedUtc { get; set; }
    }

    /// <summary>
    /// Reads and edits PCL's own update setting. PCL stores SystemSystemUpdate in
    /// "&lt;exe dir&gt;\PCL\Setup.ini" using its line-based "Key:Value" format.
    /// 3 = 关闭更新提示 / 不自动更新; 0 = 自动下载; 1 = 提示; 2 = 仅重大漏洞提示.
    /// </summary>
    public static class AutoUpdateSettings
    {
        public const string KeyName = "SystemSystemUpdate";
        public const string DisabledValue = "3";
        public const string AutoDownloadValue = "0";

        private const string BackupSuffix = ".pclfix-backup";
        private const string SidecarSuffix = ".pclfix-update.json";

        private static readonly Regex KeyLine = new Regex(
            @"^SystemSystemUpdate[ \t]*:[^\r\n]*", RegexOptions.Multiline | RegexOptions.Compiled);

        public static string GetSetupIniPath(string executablePath)
        {
            var full = Path.GetFullPath(executablePath);
            var directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            return Path.Combine(directory ?? "", "PCL", "Setup.ini");
        }

        public static AutoUpdateInfo Read(string executablePath)
        {
            var info = new AutoUpdateInfo
            {
                SetupIniPath = GetSetupIniPath(executablePath),
                State = AutoUpdateState.NotConfigured
            };

            try
            {
                if (!File.Exists(info.SetupIniPath)) return info;

                info.SetupIniExists = true;
                var text = ReadText(info.SetupIniPath).Text;

                string raw;
                if (!TryFindValue(text, out raw))
                    return info;

                info.KeyPresent = true;
                info.RawValue = raw;
                int parsed;
                if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    info.Value = parsed;
                    switch (parsed)
                    {
                        case 0: info.State = AutoUpdateState.AutoDownload; break;
                        case 1: info.State = AutoUpdateState.Notify; break;
                        case 2: info.State = AutoUpdateState.ImportantOnly; break;
                        case 3: info.State = AutoUpdateState.Disabled; break;
                        default: info.State = AutoUpdateState.Unknown; break;
                    }
                }
                else
                {
                    info.State = AutoUpdateState.Unknown;
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
                info.State = AutoUpdateState.Unknown;
            }

            return info;
        }

        public static AutoUpdateChange Disable(string executablePath, bool createBackup, Action<LogEntry> log)
        {
            var full = Path.GetFullPath(executablePath);
            if (!File.Exists(full)) throw new PatchException("找不到 PCL 文件：" + full);
            PclProcess.EnsureNotRunning(full);

            var setup = GetSetupIniPath(full);
            var result = new AutoUpdateChange { TargetPath = full, SetupIniPath = setup };

            using (TargetLock.Acquire(setup, TimeSpan.FromSeconds(10)))
            {
                var before = Read(full);
                if (!string.IsNullOrEmpty(before.Error))
                    throw new PatchException("读取 PCL 设置失败：" + before.Error);

                if (before.State == AutoUpdateState.Disabled)
                {
                    result.Success = true;
                    result.AlreadyInState = true;
                    result.CurrentValue = before.RawValue;
                    result.Message = "PCL 自动更新已经是关闭状态，无需重复操作。";
                    Log.Write(log, LogLevel.Info, result.Message);
                    return result;
                }

                var file = File.Exists(setup) ? ReadText(setup) : new TextFile { Text = "", Encoding = new UTF8Encoding(false) };
                var updated = SetValue(file.Text, DisabledValue);
                if (string.Equals(updated, file.Text, StringComparison.Ordinal))
                {
                    result.Success = true;
                    result.AlreadyInState = true;
                    result.Message = "PCL 自动更新已经是关闭状态，无需重复操作。";
                    return result;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(setup));

                if (createBackup)
                {
                    if (File.Exists(setup))
                    {
                        var backup = setup + BackupSuffix;
                        if (!File.Exists(backup)) File.Copy(setup, backup, false);
                        result.BackupPath = backup;
                    }

                    WriteSidecar(setup, before);
                }

                WriteText(setup, updated, file.Encoding);
                var after = Read(full);

                result.Success = true;
                result.Changed = true;
                result.PreviousValue = before.RawValue;
                result.CurrentValue = after.RawValue;
                result.Message = "已关闭 PCL 自动更新；PCL 将停留在当前版本。";
                Log.Write(log, LogLevel.Success, result.Message);
                Log.Write(log, LogLevel.Info, "设置文件：" + setup);
                if (result.BackupPath != null)
                    Log.Write(log, LogLevel.Info, "设置备份：" + result.BackupPath);
                return result;
            }
        }

        public static AutoUpdateChange Enable(string executablePath, Action<LogEntry> log)
        {
            var full = Path.GetFullPath(executablePath);
            if (!File.Exists(full)) throw new PatchException("找不到 PCL 文件：" + full);
            PclProcess.EnsureNotRunning(full);

            var setup = GetSetupIniPath(full);
            var result = new AutoUpdateChange { TargetPath = full, SetupIniPath = setup };

            using (TargetLock.Acquire(setup, TimeSpan.FromSeconds(10)))
            {
                var before = Read(full);
                if (!string.IsNullOrEmpty(before.Error))
                    throw new PatchException("读取 PCL 设置失败：" + before.Error);

                var sidecar = ReadSidecar(setup);
                if (sidecar == null && !before.KeyPresent)
                {
                    result.Success = true;
                    result.AlreadyInState = true;
                    result.Message = "PCL 自动更新没有被本工具关闭，无需恢复。";
                    return result;
                }
                if (sidecar == null && before.State != AutoUpdateState.Disabled)
                {
                    result.Success = true;
                    result.AlreadyInState = true;
                    result.Message = "PCL 自动更新当前未关闭，无需恢复。";
                    return result;
                }

                var file = File.Exists(setup) ? ReadText(setup) : new TextFile { Text = "", Encoding = new UTF8Encoding(false) };
                string updated;
                if (sidecar != null && sidecar.PreviousPresent &&
                    !string.Equals(sidecar.PreviousValue, DisabledValue, StringComparison.Ordinal))
                {
                    updated = SetValue(file.Text, sidecar.PreviousValue ?? AutoDownloadValue);
                }
                else
                {
                    updated = RemoveValue(file.Text);
                }

                if (string.Equals(updated, file.Text, StringComparison.Ordinal))
                {
                    DeleteSidecar(setup);
                    result.Success = true;
                    result.AlreadyInState = true;
                    result.Message = "PCL 自动更新当前未关闭，无需恢复。";
                    return result;
                }

                WriteText(setup, updated, file.Encoding);
                DeleteSidecar(setup);
                var after = Read(full);

                result.Success = true;
                result.Changed = true;
                result.PreviousValue = before.RawValue;
                result.CurrentValue = after.RawValue;
                result.Message = "已恢复 PCL 的自动更新设置。";
                Log.Write(log, LogLevel.Success, result.Message);
                Log.Write(log, LogLevel.Info, "设置文件：" + setup);
                return result;
            }
        }

        private static bool TryFindValue(string text, out string raw)
        {
            raw = null;
            var found = false;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var index = line.IndexOf(':');
                    if (index <= 0) continue;
                    var key = line.Substring(0, index).TrimStart('\uFEFF');
                    if (!string.Equals(key, KeyName, StringComparison.Ordinal)) continue;
                    raw = line.Substring(index + 1);
                    found = true;
                }
            }
            return found;
        }

        private static string SetValue(string text, string value)
        {
            var replacement = KeyName + ":" + value;
            if (KeyLine.IsMatch(text)) return KeyLine.Replace(text, replacement);

            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var builder = new StringBuilder(text);
            if (builder.Length > 0 && builder[builder.Length - 1] != '\n') builder.Append(newline);
            builder.Append(replacement).Append(newline);
            return builder.ToString();
        }

        private static string RemoveValue(string text)
        {
            var pattern = new Regex(@"^SystemSystemUpdate[ \t]*:[^\r\n]*(?:\r?\n)?",
                RegexOptions.Multiline);
            return pattern.Replace(text, "");
        }

        private static void WriteSidecar(string setupPath, AutoUpdateInfo before)
        {
            var sidecar = new AutoUpdateBackup
            {
                Key = KeyName,
                PreviousPresent = before.KeyPresent,
                PreviousValue = before.RawValue,
                CreatedUtc = DateTime.UtcNow.ToString("o")
            };

            var path = setupPath + SidecarSuffix;
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(AutoUpdateBackup));
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    serializer.WriteObject(stream, sidecar);
                FileOps.Replace(temp, path);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }

        private static AutoUpdateBackup ReadSidecar(string setupPath)
        {
            var path = setupPath + SidecarSuffix;
            if (!File.Exists(path)) return null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var serializer = new DataContractJsonSerializer(typeof(AutoUpdateBackup));
                    return (AutoUpdateBackup)serializer.ReadObject(stream);
                }
            }
            catch
            {
                return null;
            }
        }

        private static void DeleteSidecar(string setupPath)
        {
            try
            {
                var path = setupPath + SidecarSuffix;
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }

        private static TextFile ReadText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new TextFile { Text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), Encoding = new UTF8Encoding(true) };
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return new TextFile { Text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding = Encoding.Unicode };
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return new TextFile { Text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding = Encoding.BigEndianUnicode };
            return new TextFile { Text = new UTF8Encoding(false).GetString(bytes), Encoding = new UTF8Encoding(false) };
        }

        private static void WriteText(string path, string text, Encoding encoding)
        {
            var preamble = encoding.GetPreamble();
            var body = encoding.GetBytes(text);
            var bytes = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);

            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                FileOps.Replace(temp, path);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); } catch { }
                }
            }
        }

        private sealed class TextFile
        {
            public string Text;
            public Encoding Encoding;
        }
    }
}

