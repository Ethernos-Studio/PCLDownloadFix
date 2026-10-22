using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace PCLDownloadFix.Gui
{
    public partial class FclWindow : Window
    {
        private string _outputPath;

        public FclWindow()
        {
            InitializeComponent();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 FCL APK",
                Filter = "Android APK|*.apk",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) == true)
                PathBox.Text = dialog.FileName;
        }

        private async void Patch_Click(object sender, RoutedEventArgs e)
        {
            var path = (PathBox.Text ?? "").Trim().Trim('"');
            if (!File.Exists(path))
            {
                SetStatus("APK 不存在。", true);
                return;
            }
            if (MessageBox.Show(this,
                    "将生成一个新的共存版 APK（包名 com.tungsten.fcl.fix，名称 FCL (Fix)），不会修改原 APK。\r\n\r\n" +
                    "新包使用新证书，不能覆盖官方 FCL；首次启动数据目录为空。是否继续？",
                    "FCL 共存修复", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            PatchButton.IsEnabled = false;
            LogBox.Clear();
            _outputPath = null;
            OutputText.Text = "";
            SetStatus("正在准备 Android 工具并解码 APK，可能需要几分钟...");

            var progress = new Progress<string>(line =>
            {
                LogBox.AppendText(line + Environment.NewLine);
                LogBox.ScrollToEnd();
                if (line.Length > 0) SetStatus(line);
            });

            var result = await Task.Run(() => FclPatcher.Patch(path, new FclPatchOptions
            {
                Log = line => ((IProgress<string>)progress).Report(line)
            }));

            PatchButton.IsEnabled = true;
            _outputPath = result.OutputPath;
            if (result.Success)
            {
                SetStatus("完成：" + result.Message);
                OutputText.Text = result.OutputPath;
                MessageBox.Show(this,
                    "已生成共存版：\r\n" + result.OutputPath +
                    "\r\n\r\n请把 APK 安装到 Android 设备。由于使用新证书，不能覆盖官方 FCL；官方 FCL 仍可保留。",
                    "FCL 共存修复", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                SetStatus("失败：" + result.Message, true);
                if (!string.IsNullOrEmpty(result.LogPath))
                    LogBox.AppendText(Environment.NewLine + "日志：" + result.LogPath);
                MessageBox.Show(this, result.Message, "FCL 共存修复失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenOutput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = _outputPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    path = (PathBox.Text ?? "").Trim().Trim('"');
                if (!File.Exists(path)) return;
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "打开目录", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
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
        }

        private void SetStatus(string text, bool error = false)
        {
            StatusText.Text = text ?? "";
            StatusText.Foreground = error
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(185, 28, 28))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105));
        }
    }
}
