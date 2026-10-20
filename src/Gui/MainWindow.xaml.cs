using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using Microsoft.Win32;

namespace PCLDownloadFix.Gui
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<LogItem> _logs = new ObservableCollection<LogItem>();
        private readonly Patcher _patcher;
        private readonly Stopwatch _stopwatch = new Stopwatch();

        private PclTargetInfo _info;
        private string _selectedPath;
        private readonly string _initialPath;
        private bool _busy;

        public MainWindow() : this(null)
        {
        }

        public MainWindow(string initialPath)
        {
            InitializeComponent();
            _initialPath = initialPath;
            LogList.ItemsSource = _logs;
            _patcher = new Patcher(OnPatchLog);
            ClearInfo();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var detected = !string.IsNullOrWhiteSpace(_initialPath) && File.Exists(_initialPath)
                ? Path.GetFullPath(_initialPath)
                : AutoDetect();
            if (detected != null)
            {
                AddLog(new LogEntry(LogLevel.Info, "自动检测到 PCL：" + detected));
                await SelectPathAsync(detected);
            }
            else
            {
                SetStatus("未自动找到 PCL，请点击“浏览...”选择 Plain Craft Launcher 2.exe");
                AddLog(new LogEntry(LogLevel.Info, "未自动找到 PCL，可以手动选择或把 EXE 拖进窗口。"));
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_busy) return;
            e.Cancel = true;
            MessageBox.Show(this, "正在执行修复或校验，请等待当前操作完成。", "请稍候",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void Browse_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var dialog = new OpenFileDialog
            {
                Title = "选择 PCL 主程序",
                Filter = "PCL 启动器|Plain Craft Launcher 2.exe|可执行文件|*.exe|所有文件|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) == true)
                await SelectPathAsync(dialog.FileName);
        }

        private async Task SelectPathAsync(string path)
        {
            if (_busy || string.IsNullOrWhiteSpace(path)) return;
            SetBusy(true, "正在读取文件信息...");
            try
            {
                await RefreshInfoAsync(path);
                AddLog(new LogEntry(LogLevel.Info,
                    "已载入 " + _info.BuildLabel + " · " + _info.StateText + " · " + HashUtil.Short(_info.Sha256)));
                SetStatus("文件已载入：" + _info.StateText);
            }
            catch (Exception ex)
            {
                _info = null;
                ClearInfo();
                PathBox.Text = path;
                var message = ex is PatchException ? ex.Message : "读取文件失败：" + ex.Message;
                AddLog(new LogEntry(LogLevel.Error, message));
                SetBadge("读取失败", Color.FromRgb(0xFE, 0xE2, 0xE2), Color.FromRgb(0x99, 0x1B, 0x1B));
                SetStatus("无法读取该文件");
                MessageBox.Show(this, message, "无法读取文件", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task RefreshInfoAsync(string path)
        {
            _selectedPath = path;
            PathBox.Text = path;
            PathBox.ToolTip = path;
            var info = await Task.Run(() => _patcher.Inspect(path));
            _info = info;
            UpdateInfo(info);
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _info == null) return;
            if (_info.State != PatchState.Original && _info.State != PatchState.Partial)
            {
                MessageBox.Show(this, "当前文件不需要修复，或状态无法识别。", "无需修复",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirmation = "即将修改：\r\n" + _info.Path + "\r\n\r\n" +
                               (_info.BackupExists && _info.BackupIsValidOriginal
                                   ? "原版备份已经存在，将保留该备份。"
                                   : "工具会先创建原版备份（.pcl-original.bak）。") +
                               "\r\n修改后可用“还原原版”恢复。\r\n\r\n是否继续？";
            if (MessageBox.Show(this, confirmation, "确认应用下载修复",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            SetBusy(true, "正在应用修复...");
            _stopwatch.Restart();
            try
            {
                var options = new PatchOptions
                {
                    CreateBackup = BackupCheck.IsChecked == true,
                    Verify = VerifyCheck.IsChecked == true
                };
                var path = _info.Path;
                var result = await Task.Run(() => _patcher.Apply(path, options));

                if (result.AlreadyPatched)
                {
                    AddLog(new LogEntry(LogLevel.Success, "该文件已经修复，无需重复操作。"));
                }
                else
                {
                    AddLog(new LogEntry(LogLevel.Success, "修复完成：" + result.OutputPath));
                    if (!string.IsNullOrEmpty(result.BackupPath))
                        AddLog(new LogEntry(LogLevel.Info, "原版备份：" + result.BackupPath));
                }

                await RefreshInfoAsync(path);
                SetStatus("修复完成");

                MessageBox.Show(this,
                    "下载功能已恢复。\r\n\r\n输出文件：\r\n" + result.OutputPath +
                    (string.IsNullOrEmpty(result.BackupPath) ? "" : "\r\n\r\n原版备份：\r\n" + result.BackupPath) +
                    "\r\n\r\n启动 PCL 后打开“下载”页面即可使用。",
                    "修复完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                var message = ex is PatchException ? ex.Message : "修复失败：" + ex.Message;
                AddLog(new LogEntry(LogLevel.Error, message));
                SetStatus("修复失败");
                MessageBox.Show(this, message, "修复失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _stopwatch.Stop();
                ElapsedText.Text = "用时 " + _stopwatch.Elapsed.TotalSeconds.ToString("0.0") + " 秒";
                SetBusy(false);
            }
        }

        private async void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _info == null) return;
            if (_info.State != PatchState.Patched && _info.State != PatchState.Partial)
            {
                MessageBox.Show(this, "当前文件不是已修复状态，无需还原。", "无需还原",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!_info.BackupIsValidOriginal)
            {
                MessageBox.Show(this, "找不到有效的原版备份，无法安全还原。\r\n\r\n备份位置：\r\n" + _info.BackupPath,
                    "无法还原", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MessageBox.Show(this,
                    "即将把目标文件恢复为原版 PCL：\r\n" + _info.Path +
                    "\r\n\r\n将使用备份：\r\n" + _info.BackupPath + "\r\n\r\n是否继续？",
                    "确认还原原版", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            SetBusy(true, "正在还原原版...");
            _stopwatch.Restart();
            try
            {
                var path = _info.Path;
                var result = await Task.Run(() => _patcher.Restore(path, false));
                AddLog(new LogEntry(LogLevel.Success, result.AlreadyOriginal ? "当前已经是原版。" : "已还原为原版 PCL。"));
                await RefreshInfoAsync(path);
                SetStatus("已还原原版");
                MessageBox.Show(this, result.AlreadyOriginal ? "当前已经是原版。" : "已还原为原版 PCL。",
                    "还原完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                var message = ex is PatchException ? ex.Message : "还原失败：" + ex.Message;
                AddLog(new LogEntry(LogLevel.Error, message));
                SetStatus("还原失败");
                MessageBox.Show(this, message, "还原失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _stopwatch.Stop();
                ElapsedText.Text = "用时 " + _stopwatch.Elapsed.TotalSeconds.ToString("0.0") + " 秒";
                SetBusy(false);
            }
        }

        private async void Verify_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _info == null) return;
            await SelectPathAsync(_info.Path);
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var path = _info != null ? _info.Path : _selectedPath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开目录失败：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            _logs.Clear();
        }

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(string.Join("\r\n", _logs.Select(item => item.ToLine())));
                SetStatus("日志已复制到剪贴板");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "复制失败：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveLog_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存日志",
                Filter = "文本文件|*.txt|所有文件|*.*",
                FileName = "PCLDownloadFix-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(dialog.FileName, string.Join("\r\n", _logs.Select(item => item.ToLine())));
                SetStatus("日志已保存：" + dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SelfCheck_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = SelfCheck.Run();
                AddLog(new LogEntry(LogLevel.Info, "程序 SHA-256：" + result.ExeSha256));
                AddLog(new LogEntry(result.AllEmbeddedOk ? LogLevel.Success : LogLevel.Error,
                    "内嵌组件校验：" + (result.AllEmbeddedOk ? "通过" : "失败")));

                var text = "程序路径：\r\n" + result.ExePath +
                           "\r\n\r\n文件大小：" + HashUtil.FormatSize(result.Size) +
                           "\r\n\r\nEXE SHA-256：\r\n" + result.ExeSha256 +
                           "\r\n\r\n内嵌恢复模板：" + (result.DonorOk ? "通过" : "失败") +
                           "\r\n内嵌 Mono.Cecil：" + (result.CecilOk ? "通过" : "失败") +
                           "\r\n\r\n请将上面的 EXE SHA-256 与 GitHub Release 或 SHA256SUMS.txt 中公布的值比对；一致即表示文件未被修改。\r\n" +
                           DisclaimerText.ProjectUrl;
                MessageBox.Show(this, text, "本程序完整性",
                    MessageBoxButton.OK, result.AllEmbeddedOk ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AddLog(new LogEntry(LogLevel.Error, "本程序校验失败：" + ex.Message));
                MessageBox.Show(this, "本程序校验失败：" + ex.Message, "校验失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // Best-effort only.
            }
            e.Handled = true;
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (!HasExecutable(e)) return;
            DropOverlay.Visibility = Visibility.Visible;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasExecutable(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!HasExecutable(e)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var exe = files == null ? null : files.FirstOrDefault(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (exe == null) return;
            await SelectPathAsync(exe);
        }

        private static bool HasExecutable(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            return files != null && files.Any(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }

        private void OnPatchLog(LogEntry entry)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<LogEntry>(OnPatchLog), entry);
                return;
            }
            AddLog(entry);
            if (entry.Level == LogLevel.Step || entry.Level == LogLevel.Success)
                SetStatus(entry.Message);
        }

        private void AddLog(LogEntry entry)
        {
            var item = new LogItem(entry);
            _logs.Add(item);
            if (_logs.Count > 5000) _logs.RemoveAt(0);
            LogList.ScrollIntoView(item);
        }

        private void UpdateInfo(PclTargetInfo info)
        {
            VersionValue.Text = (string.IsNullOrEmpty(info.FileVersion) ? info.AssemblyVersion : info.FileVersion) +
                                "  ·  " + info.BuildLabel;
            SizeValue.Text = HashUtil.FormatSize(info.Size);
            HashValue.Text = info.Sha256;
            BackupValue.Text = info.BackupText;
            StateDetail.Text = info.Message;
            BackupCheck.IsEnabled = !info.BackupExists;
            if (info.BackupExists) BackupCheck.IsChecked = true;

            switch (info.State)
            {
                case PatchState.Original:
                    SetBadge("原版 · 待修复", Color.FromRgb(0xFE, 0xF3, 0xC7), Color.FromRgb(0x92, 0x40, 0x0E));
                    break;
                case PatchState.Patched:
                    if (info.NoFixNeeded)
                        SetBadge("无需修复", Color.FromRgb(0xDC, 0xFC, 0xE7), Color.FromRgb(0x16, 0x65, 0x34));
                    else
                        SetBadge("已修复", Color.FromRgb(0xDC, 0xFC, 0xE7), Color.FromRgb(0x16, 0x65, 0x34));
                    break;
                case PatchState.Partial:
                    SetBadge("部分修复", Color.FromRgb(0xFF, 0xED, 0xD5), Color.FromRgb(0x9A, 0x34, 0x12));
                    break;
                default:
                    SetBadge("不支持", Color.FromRgb(0xFE, 0xE2, 0xE2), Color.FromRgb(0x99, 0x1B, 0x1B));
                    break;
            }

            ApplyButton.Content = info.State == PatchState.Patched
                ? (info.NoFixNeeded ? "无需修复" : "已修复")
                : "一键修复";
            ApplyButton.IsEnabled = !_busy && (info.State == PatchState.Original || info.State == PatchState.Partial);
            RestoreButton.IsEnabled = !_busy && info.State == PatchState.Patched && info.BackupIsValidOriginal;
            VerifyButton.IsEnabled = !_busy;
            OpenFolderButton.IsEnabled = !_busy;
        }

        private void ClearInfo()
        {
            VersionValue.Text = "—";
            SizeValue.Text = "—";
            HashValue.Text = "—";
            BackupValue.Text = "—";
            StateDetail.Text = "";
            SetBadge("等待选择", Color.FromRgb(0xF1, 0xF5, 0xF9), Color.FromRgb(0x47, 0x55, 0x69));
            ApplyButton.IsEnabled = false;
            ApplyButton.Content = "一键修复";
            RestoreButton.IsEnabled = false;
            VerifyButton.IsEnabled = false;
            OpenFolderButton.IsEnabled = false;
        }

        private void SetBadge(string text, Color background, Color foreground)
        {
            StateBadge.Background = new SolidColorBrush(background);
            StateText.Foreground = new SolidColorBrush(foreground);
            StateText.Text = text;
        }

        private void SetBusy(bool busy, string status = null)
        {
            _busy = busy;
            Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Mouse.OverrideCursor = busy ? Cursors.Wait : null;
            BrowseButton.IsEnabled = !busy;
            PathBox.IsEnabled = !busy;
            if (status != null) SetStatus(status);

            if (_info != null)
            {
                ApplyButton.IsEnabled = !busy && (_info.State == PatchState.Original || _info.State == PatchState.Partial);
                RestoreButton.IsEnabled = !busy && _info.State == PatchState.Patched && _info.BackupIsValidOriginal;
            }
            else
            {
                ApplyButton.IsEnabled = false;
                RestoreButton.IsEnabled = false;
            }
            VerifyButton.IsEnabled = !busy && _info != null;
            OpenFolderButton.IsEnabled = !busy && _info != null;
        }

        private void SetStatus(string text)
        {
            StatusText.Text = text ?? "";
        }

        private static string AutoDetect()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "Plain Craft Launcher 2.exe"),
                Path.Combine(Directory.GetCurrentDirectory(), "Plain Craft Launcher 2.exe"),
                Path.Combine(baseDir, "..", "Plain Craft Launcher 2.exe")
            };
            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch
                {
                }
            }
            return null;
        }
    }

    public sealed class LogItem
    {
        public LogItem(LogEntry entry)
        {
            TimeText = entry.Time.ToString("HH:mm:ss");
            LevelText = entry.LevelText;
            Message = entry.Message;
            LevelBrush = BrushFor(entry.Level);
        }

        public string TimeText { get; private set; }
        public string LevelText { get; private set; }
        public string Message { get; private set; }
        public Brush LevelBrush { get; private set; }

        public string ToLine()
        {
            return "[" + TimeText + "] [" + LevelText + "] " + Message;
        }

        private static Brush BrushFor(LogLevel level)
        {
            Color color;
            switch (level)
            {
                case LogLevel.Step: color = Color.FromRgb(0x08, 0x91, 0xB2); break;
                case LogLevel.Success: color = Color.FromRgb(0x16, 0xA3, 0x4A); break;
                case LogLevel.Warn: color = Color.FromRgb(0xD9, 0x77, 0x06); break;
                case LogLevel.Error: color = Color.FromRgb(0xDC, 0x26, 0x26); break;
                case LogLevel.Detail: color = Color.FromRgb(0x94, 0xA3, 0xB8); break;
                default: color = Color.FromRgb(0x3B, 0x82, 0xF6); break;
            }
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}

