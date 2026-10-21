using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;

namespace PCLDownloadFix.Gui
{
    public partial class BatchWindow : Window
    {
        private readonly ObservableCollection<BatchItem> _items = new ObservableCollection<BatchItem>();
        private readonly List<string> _scanRoots = new List<string>();
        private readonly Patcher _patcher;
        private CancellationTokenSource _cts;
        private bool _busy;

        public BatchWindow()
        {
            InitializeComponent();
            InstanceList.ItemsSource = _items;
            _patcher = new Patcher(delegate { });
            UpdateScanRootsText();
            UpdateScanModeHint();
            UpdateCounts();
        }

        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            var roots = _scanRoots.ToList();
            var mode = ScanModeCombo.SelectedIndex;
            var paths = new List<string>();
            var sources = new List<string>();
            string errorNote = null;

            _cts = new CancellationTokenSource();
            var stopwatch = Stopwatch.StartNew();
            SetBusy(true, "正在扫描...");
            try
            {
                if (mode == 0)
                {
                    SetStatus("正在查询 Windows 搜索索引...");
                    var search = await Task.Run(() =>
                        WindowsSearchScanner.FindPclExecutables(TimeSpan.FromSeconds(10), _cts.Token));

                    if (!string.IsNullOrEmpty(search.Error))
                    {
                        errorNote = "Windows 搜索索引不可用：" + search.Error;
                    }
                    else if (search.Paths.Count > 0)
                    {
                        paths.AddRange(search.Paths);
                        sources.Add("Windows 搜索索引");
                    }

                    if (roots.Count > 0)
                        await ScanFoldersAsync(roots, 16, paths, sources, "指定文件夹");
                    else if (paths.Count == 0)
                        await ScanFoldersAsync(PclScanner.DefaultRoots(), 4, paths, sources, "常见目录");
                }
                else if (mode == 1)
                {
                    SetStatus("正在读取 NTFS MFT（需要管理员权限）...");
                    var mftProgress = new Progress<NtfsMftProgress>(p =>
                        SetStatus("MFT " + p.Volume + "：" + p.Records + " 条记录，匹配 " + p.Matches + " 个..."));
                    var mft = await Task.Run(() => NtfsMftScanner.Scan(
                        _cts.Token,
                        p => ((IProgress<NtfsMftProgress>)mftProgress).Report(p)), _cts.Token);

                    if (mft.Paths.Count > 0)
                    {
                        paths.AddRange(mft.Paths);
                        sources.Add("NTFS MFT");
                    }
                    if (!string.IsNullOrEmpty(mft.Error))
                    {
                        errorNote = mft.Error;
                        SetStatus("MFT 扫描失败：" + mft.Error);
                    }

                    if (roots.Count > 0)
                        await ScanFoldersAsync(roots, 16, paths, sources, "指定文件夹");

                    if (paths.Count == 0)
                    {
                        SetStatus("MFT 不可用，尝试 Windows 搜索索引...");
                        var fallback = await Task.Run(() =>
                            WindowsSearchScanner.FindPclExecutables(TimeSpan.FromSeconds(10), _cts.Token));
                        if (string.IsNullOrEmpty(fallback.Error) && fallback.Paths.Count > 0)
                        {
                            paths.AddRange(fallback.Paths);
                            sources.Add("Windows 搜索索引（MFT 回退）");
                        }
                        else if (!string.IsNullOrEmpty(fallback.Error))
                        {
                            errorNote = errorNote ?? fallback.Error;
                        }
                    }

                    if (paths.Count == 0)
                        await ScanFoldersAsync(PclScanner.DefaultRoots(), 4, paths, sources, "常见目录（MFT 回退）");
                }
                else
                {
                    if (roots.Count > 0)
                        await ScanFoldersAsync(roots, 16, paths, sources, "指定文件夹");
                    else
                        await ScanFoldersAsync(PclScanner.DefaultRoots(), 4, paths, sources, "常见目录");
                }

                var unique = paths
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => { try { return Path.GetFullPath(p); } catch { return null; } })
                    .Where(p => p != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _items.Clear();
                foreach (var path in unique) AddPlaceholder(path);
                await ProbeItemsAsync(_items.ToList(), "正在识别版本...");

                var message = "扫描完成：找到 " + _items.Count + " 个 PCL 实例" +
                    (sources.Count > 0 ? "（" + string.Join("、", sources) + "，" : "（") +
                    stopwatch.Elapsed.TotalSeconds.ToString("0.0") + " 秒）。";
                SetStatus(message + (string.IsNullOrEmpty(errorNote) ? "" : " " + errorNote));
            }
            catch (OperationCanceledException)
            {
                SetStatus("扫描已取消。");
            }
            catch (Exception ex)
            {
                SetStatus("扫描失败：" + ex.Message);
            }
            finally
            {
                SetBusy(false);
                _cts.Dispose();
                _cts = null;
            }
        }

        private async Task ScanFoldersAsync(IList<string> roots, int maxDepth, List<string> paths,
            List<string> sources, string label)
        {
            if (roots == null || roots.Count == 0) return;
            SetStatus("正在扫描" + label + "（" + roots.Count + " 个文件夹）...");
            var progress = new Progress<PclScanProgress>(p =>
                SetStatus("正在扫描：" + p.Directories + " 个目录，找到 " + p.Found + " 个 PCL 实例..."));
            var scanned = await Task.Run(() => PclScanner.Scan(
                roots, maxDepth, _cts.Token,
                p => ((IProgress<PclScanProgress>)progress).Report(p)), _cts.Token);
            paths.AddRange(scanned);
            if (!sources.Contains(label)) sources.Add(label);
        }

        private void ScanMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateScanModeHint();
        }

        private void UpdateScanModeHint()
        {
            if (ScanModeHint == null) return;
            switch (ScanModeCombo == null ? 0 : ScanModeCombo.SelectedIndex)
            {
                case 1:
                    ScanModeHint.Text = "MFT 完整且快，但需要管理员权限；非 NTFS 分区会跳过。";
                    break;
                case 2:
                    ScanModeHint.Text = "逐目录遍历，兼容所有文件系统但较慢；建议添加扫描文件夹。";
                    break;
                default:
                    ScanModeHint.Text = "最快，但只能找到 Windows 已建立索引的位置。";
                    break;
            }
        }

        private void AddScanFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            using (var dialog = new FolderBrowserDialog
            {
                Description = "选择要扫描的文件夹（可重复添加多个）",
                ShowNewFolderButton = false
            })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var path = Path.GetFullPath(dialog.SelectedPath);
                if (!_scanRoots.Contains(path, StringComparer.OrdinalIgnoreCase))
                    _scanRoots.Add(path);
                UpdateScanRootsText();
            }
        }

        private void ClearScanFolders_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _scanRoots.Clear();
            UpdateScanRootsText();
        }

        private void UpdateScanRootsText()
        {
            if (ScanRootsBox == null) return;
            ScanRootsBox.Text = _scanRoots.Count == 0
                ? "（未指定；按扫描方式使用索引或常见目录）"
                : string.Join("; ", _scanRoots);
            ScanRootsBox.ToolTip = ScanRootsBox.Text;
        }

        private async void Add_Click(object sender, RoutedEventArgs e)
        {
            var paths = SplitAddresses(AddressBox.Text).ToList();
            if (paths.Count == 0)
            {
                SetStatus("请先粘贴一个或多个路径（换行或分号分隔）。");
                return;
            }

            AddressBox.Clear();
            await AddPathsAsync(paths);
        }

        private async Task AddPathsAsync(IEnumerable<string> paths)
        {
            if (_busy) return;

            var added = new List<BatchItem>();
            var errors = new List<string>();
            foreach (var input in paths)
            {
                var path = (input ?? "").Trim().Trim('"');
                if (string.IsNullOrEmpty(path)) continue;

                if (Directory.Exists(path))
                {
                    var found = await Task.Run(() => PclScanner.FindInDirectory(path, 3, CancellationToken.None));
                    if (found.Count == 0)
                        errors.Add("目录中没有找到 PCL：" + path);
                    foreach (var file in found)
                        AddIfNew(file, added);
                }
                else if (File.Exists(path))
                {
                    AddIfNew(path, added);
                }
                else
                {
                    errors.Add("路径不存在：" + path);
                }
            }

            if (added.Count == 0)
            {
                SetStatus(errors.Count > 0 ? errors[0] : "没有添加任何文件。");
                return;
            }

            await ProbeItemsAsync(added, "正在识别添加的实例...");
            SetStatus("已添加 " + added.Count + " 个实例" + (errors.Count > 0 ? "，" + errors.Count + " 个路径无法添加。" : "。"));
        }

        private void AddIfNew(string path, List<BatchItem> added)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }

            if (_items.Any(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase))) return;
            var item = new BatchItem(full);
            item.PropertyChanged += OnItemPropertyChanged;
            _items.Add(item);
            added.Add(item);
        }

        private void AddPlaceholder(string path)
        {
            var item = new BatchItem(path);
            item.PropertyChanged += OnItemPropertyChanged;
            _items.Add(item);
        }

        private async Task ProbeItemsAsync(IList<BatchItem> items, string status)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (_cts != null) _cts.Token.ThrowIfCancellationRequested();
                var item = items[i];
                SetStatus(status + "（" + (i + 1) + "/" + items.Count + "）");

                var probe = await Task.Run(() => BuildProbe(item.Path));
                ApplyProbe(item, probe);
            }
            UpdateCounts();
        }

        private static BatchProbe BuildProbe(string path)
        {
            var probe = new BatchProbe();
            try
            {
                var version = FileVersionInfo.GetVersionInfo(path);
                probe.FileVersion = version.FileVersion ?? "";
            }
            catch
            {
                probe.FileVersion = "";
            }
            probe.Build = PclBuildInfo.Probe(path);
            return probe;
        }

        private static void ApplyProbe(BatchItem item, BatchProbe probe)
        {
            item.FileVersion = probe.FileVersion;
            item.VersionCode = probe.Build.VersionCode;
            item.BuildType = probe.Build.BuildType;
            item.BuildKey = probe.Build.BuildKey;
            item.VersionText = FormatVersion(probe.Build.BuildKey, probe.FileVersion);
            item.StateText = string.IsNullOrEmpty(probe.Build.Error) ? "未检测" : "读取失败";
            item.ActionText = ActionTextFor(probe.Build.VersionCode, null);
        }

        private async void Detect_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (_items.Count == 0)
            {
                SetStatus("列表为空，请先扫描或添加实例。");
                return;
            }

            _cts = new CancellationTokenSource();
            SetBusy(true, "正在检测状态...");
            try
            {
                for (var i = 0; i < _items.Count; i++)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var item = _items[i];
                    SetStatus("正在检测：" + (i + 1) + "/" + _items.Count + " " + item.Path);

                    try
                    {
                        var info = await Task.Run(() => _patcher.Inspect(item.Path));
                        ApplyInfo(item, info);
                    }
                    catch (Exception ex)
                    {
                        item.StateText = "读取失败";
                        item.AutoUpdateText = "—";
                        item.ActionText = "跳过";
                        item.Error = ex.Message;
                    }
                }
                SetStatus("状态检测完成。");
            }
            catch (OperationCanceledException)
            {
                SetStatus("状态检测已取消。");
            }
            finally
            {
                SetBusy(false);
                _cts.Dispose();
                _cts = null;
            }
        }

        private async void Process_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var selected = _items.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0)
            {
                SetStatus("请先勾选要处理的实例。");
                return;
            }

            _cts = new CancellationTokenSource();
            SetBusy(true, "正在准备处理...");
            try
            {
                // Make sure every selected item has enough information to choose
                // between "disable update" (< 413) and "patch download" (>= 413).
                for (var i = 0; i < selected.Count; i++)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var item = selected[i];
                    if (item.VersionCode.HasValue && item.PatchState != PatchState.Unknown) continue;

                    SetStatus("正在读取：" + (i + 1) + "/" + selected.Count);
                    try
                    {
                        var info = await Task.Run(() => _patcher.Inspect(item.Path));
                        ApplyInfo(item, info);
                    }
                    catch (Exception ex)
                    {
                        item.StateText = "读取失败";
                        item.ActionText = "跳过";
                        item.Error = ex.Message;
                    }
                }

                var plans = selected.Select(i => new BatchPlan
                {
                    Item = i,
                    Action = DecideAction(i)
                }).ToList();

                var disableCount = plans.Count(p => p.Action == BatchAction.DisableUpdate);
                var patchCount = plans.Count(p => p.Action == BatchAction.PatchDownload);
                var skipCount = plans.Count(p => p.Action == BatchAction.Skip);

                var confirmation = "将处理 " + selected.Count + " 个实例：\r\n\r\n" +
                                   "关闭自动更新（411.50 / 412.00）：" + disableCount + " 个\r\n" +
                                   "修复下载（413.50 / 414.00）：" + patchCount + " 个\r\n" +
                                   (skipCount > 0 ? "无法识别/跳过：" + skipCount + " 个\r\n" : "") +
                                   "\r\n请确认这些 PCL 都已完全关闭。\r\n\r\n是否继续？";
                if (MessageBox.Show(this, confirmation, "确认批量处理",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }

                var success = 0;
                var failed = 0;
                for (var i = 0; i < plans.Count; i++)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var plan = plans[i];
                    var item = plan.Item;
                    SetStatus("正在处理：" + (i + 1) + "/" + plans.Count + " " + item.Path);

                    try
                    {
                        string resultText;
                        if (plan.Action == BatchAction.DisableUpdate)
                        {
                            var change = await Task.Run(() => _patcher.DisableAutoUpdate(item.Path));
                            resultText = change.Changed ? "自动更新已关闭" : "已关闭";
                        }
                        else if (plan.Action == BatchAction.PatchDownload)
                        {
                            var result = await Task.Run(() => _patcher.Apply(item.Path, new PatchOptions()));
                            resultText = result.AlreadyPatched ? "无需修复" : "修复完成";
                        }
                        else
                        {
                            item.StateText = "跳过";
                            item.Error = "版本无法识别，未执行处理。";
                            failed++;
                            continue;
                        }

                        var after = await Task.Run(() => _patcher.Inspect(item.Path));
                        ApplyInfo(item, after);
                        item.StateText = resultText;
                        item.Error = null;
                        success++;
                    }
                    catch (Exception ex)
                    {
                        item.StateText = "失败";
                        item.Error = ex.Message;
                        failed++;
                    }
                }

                SetStatus("批量处理完成：成功 " + success + " 个，失败 " + failed + " 个。");
                MessageBox.Show(this,
                    "批量处理完成。\r\n\r\n成功：" + success + " 个\r\n失败：" + failed + " 个" +
                    (failed > 0 ? "\r\n\r\n失败项可在列表的“状态”列查看，双击行可打开所在目录。" : ""),
                    "批量处理结果", MessageBoxButton.OK,
                    failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                SetStatus("批量处理已取消。");
            }
            finally
            {
                SetBusy(false);
                _cts.Dispose();
                _cts = null;
            }
        }

        private static BatchAction DecideAction(BatchItem item)
        {
            if (item.VersionCode.HasValue)
                return item.VersionCode.Value < 413 ? BatchAction.DisableUpdate : BatchAction.PatchDownload;

            switch (item.PatchState)
            {
                case PatchState.Original:
                case PatchState.Partial:
                    return BatchAction.PatchDownload;
                case PatchState.Unrestricted:
                    return BatchAction.DisableUpdate;
                default:
                    return BatchAction.Skip;
            }
        }

        private static void ApplyInfo(BatchItem item, PclTargetInfo info)
        {
            item.FileVersion = info.FileVersion;
            item.VersionCode = info.VersionCode;
            item.BuildType = info.BuildType;
            item.BuildKey = info.BuildKey;
            item.PatchState = info.State;
            item.VersionText = FormatVersion(info.BuildKey, info.FileVersion);
            item.StateText = StateTextFor(info);
            item.AutoUpdateText = info.AutoUpdateText;
            item.ActionText = ActionTextFor(info.VersionCode, info.State);
        }

        private static string FormatVersion(string buildKey, string fileVersion)
        {
            if (string.IsNullOrEmpty(buildKey)) return string.IsNullOrEmpty(fileVersion) ? "未知" : fileVersion;
            return string.IsNullOrEmpty(fileVersion) ? buildKey : buildKey + "  (" + fileVersion + ")";
        }

        private static string StateTextFor(PclTargetInfo info)
        {
            switch (info.State)
            {
                case PatchState.Original: return "待修复";
                case PatchState.Patched: return info.NoFixNeeded ? "无需修复" : "已修复";
                case PatchState.Unrestricted: return "无需修复";
                case PatchState.Partial: return "部分修复";
                case PatchState.Unsupported: return "不支持";
                case PatchState.Error: return "读取失败";
                default: return "未知";
            }
        }

        private static string ActionTextFor(int? versionCode, PatchState? state)
        {
            if (versionCode.HasValue)
                return versionCode.Value < 413 ? "关闭自动更新" : "修复下载";
            if (state == PatchState.Original || state == PatchState.Partial) return "修复下载";
            if (state == PatchState.Unrestricted) return "关闭自动更新";
            return "跳过";
        }

        private void OnItemPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsSelected") UpdateCounts();
        }

        private void UpdateCounts()
        {
            var selected = _items.Count(i => i.IsSelected);
            if (!_busy)
                SetStatus("共 " + _items.Count + " 个实例，已选 " + selected + " 个。");
        }

        private void SetBusy(bool busy, string status = null)
        {
            _busy = busy;
            Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ScanButton.IsEnabled = !busy;
            AddButton.IsEnabled = !busy;
            DetectButton.IsEnabled = !busy;
            ProcessButton.IsEnabled = !busy;
            CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (status != null) SetStatus(status);
        }

        private void SetStatus(string text)
        {
            StatusText.Text = text ?? "";
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) _cts.Cancel();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_busy) return;
            e.Cancel = true;
            MessageBox.Show(this, "正在扫描、检测或处理，请先等待完成或点击“取消”。", "请稍候",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static IEnumerable<string> SplitAddresses(string text)
        {
            return (text ?? "").Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().Trim('"'))
                .Where(s => s.Length > 0);
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items) item.IsSelected = true;
            UpdateCounts();
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items) item.IsSelected = false;
            UpdateCounts();
        }

        private void Invert_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items) item.IsSelected = !item.IsSelected;
            UpdateCounts();
        }

        private void SelectOld_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items)
                item.IsSelected = item.VersionCode.HasValue && item.VersionCode.Value < 413;
            UpdateCounts();
        }

        private void SelectNew_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items)
                item.IsSelected = item.VersionCode.HasValue && item.VersionCode.Value >= 413;
            UpdateCounts();
        }

        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = _items.Where(i => i.IsSelected).ToList();
            foreach (var item in selected) _items.Remove(item);
            UpdateCounts();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _items.Clear();
            UpdateCounts();
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null) await AddPathsAsync(paths);
            e.Handled = true;
        }

        private void InstanceList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var item = InstanceList.SelectedItem as BatchItem;
            if (item == null) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + item.Path + "\"")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }
    }

    public sealed class BatchItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _versionText = "—";
        private string _stateText = "未检测";
        private string _autoUpdateText = "—";
        private string _actionText = "待检测";
        private string _error;

        public BatchItem(string path)
        {
            Path = path;
        }

        public string Path { get; private set; }

        public string FileVersion { get; set; }
        public int? VersionCode { get; set; }
        public int? BuildType { get; set; }
        public string BuildKey { get; set; }
        public PatchState PatchState { get; set; }
        public string Error
        {
            get { return _error; }
            set { _error = value; Raise("Error"); }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                Raise("IsSelected");
            }
        }

        public string VersionText
        {
            get { return _versionText; }
            set
            {
                if (_versionText == value) return;
                _versionText = value;
                Raise("VersionText");
            }
        }

        public string StateText
        {
            get { return _stateText; }
            set
            {
                if (_stateText == value) return;
                _stateText = value;
                Raise("StateText");
            }
        }

        public string AutoUpdateText
        {
            get { return _autoUpdateText; }
            set
            {
                if (_autoUpdateText == value) return;
                _autoUpdateText = value;
                Raise("AutoUpdateText");
            }
        }

        public string ActionText
        {
            get { return _actionText; }
            set
            {
                if (_actionText == value) return;
                _actionText = value;
                Raise("ActionText");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    internal sealed class BatchProbe
    {
        public string FileVersion;
        public PclBuildInfo Build;
    }

    internal sealed class BatchPlan
    {
        public BatchItem Item;
        public BatchAction Action;
    }

    internal enum BatchAction
    {
        Skip,
        DisableUpdate,
        PatchDownload
    }
}
