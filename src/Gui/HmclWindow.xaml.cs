using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace PCLDownloadFix.Gui
{
    public partial class HmclWindow : Window
    {
        public HmclWindow()
        {
            InitializeComponent();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 HMCL",
                Filter = "HMCL|HMCL*.jar;HMCL*.exe|JAR|*.jar|EXE|*.exe",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) == true)
            {
                PathBox.Text = dialog.FileName;
                Inspect();
            }
        }

        private void Inspect_Click(object sender, RoutedEventArgs e)
        {
            Inspect();
        }

        private void Inspect()
        {
            var path = (PathBox.Text ?? "").Trim().Trim('"');
            if (!File.Exists(path))
            {
                SetStatus("文件不存在。", true);
                return;
            }

            var info = HmclPatcher.Inspect(path);
            KindText.Text = info.Kind == HmclPackageKind.Executable ? "HMCL EXE" : "HMCL JAR";
            VersionText.Text = string.IsNullOrEmpty(info.Version) ? "未知" : info.Version;
            GateText.Text = !info.Success
                ? "读取失败"
                : !info.HasRegionGate
                    ? "不存在"
                    : info.IsPatched ? "已修复" : "存在";
            BackupText.Text = info.BackupExists ? info.BackupPath : "无";
            SetStatus(info.Success
                ? "已读取 HMCL 信息。"
                : "读取失败：" + info.Error, !info.Success);
        }

        private void Patch_Click(object sender, RoutedEventArgs e)
        {
            var path = (PathBox.Text ?? "").Trim().Trim('"');
            var info = HmclPatcher.Inspect(path);
            if (!info.Success)
            {
                SetStatus("读取失败：" + info.Error, true);
                return;
            }
            if (!info.HasRegionGate || info.IsPatched)
            {
                SetStatus(info.HasRegionGate ? "HMCL 已经修复。" : "该版本没有地区下载限制。");
                Inspect();
                return;
            }
            if (MessageBox.Show(this,
                    "请先完全关闭 HMCL。\r\n\r\n修复会替换 JAR 中的 RegionUtils 类，并创建 .hmcl-original.bak。是否继续？",
                    "修复 HMCL", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var result = HmclPatcher.Patch(path);
            SetStatus(result.Message, !result.Success);
            if (!string.IsNullOrEmpty(result.BackupPath))
                BackupText.Text = result.BackupPath;
            Inspect();
            if (!result.Success)
                MessageBox.Show(this, result.Message, "修复失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            var path = (PathBox.Text ?? "").Trim().Trim('"');
            if (MessageBox.Show(this, "请先完全关闭 HMCL。\r\n\r\n恢复会使用 .hmcl-original.bak 覆盖当前文件。是否继续？",
                    "恢复 HMCL", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var result = HmclPatcher.Restore(path);
            SetStatus(result.Message, !result.Success);
            Inspect();
            if (!result.Success)
                MessageBox.Show(this, result.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            PathBox.Text = files[0];
            Inspect();
        }

        private void SetStatus(string text, bool error = false)
        {
            StatusText.Text = text ?? "";
            StatusText.Foreground = error
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(185, 28, 28))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139));
        }
    }
}
