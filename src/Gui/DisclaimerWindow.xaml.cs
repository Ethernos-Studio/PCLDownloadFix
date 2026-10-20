using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;

namespace PCLDownloadFix.Gui
{
    public partial class DisclaimerWindow : Window
    {
        public DisclaimerWindow()
        {
            InitializeComponent();
            DisclaimerBody.Text = DisclaimerText.FullText;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Activate();
            AgreeCheck.Focus();
        }

        private void AgreeCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (ContinueButton != null)
                ContinueButton.IsEnabled = AgreeCheck.IsChecked == true;
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            if (AgreeCheck.IsChecked != true) return;
            DialogResult = true;
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
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
                // Opening the browser is best-effort.
            }
            e.Handled = true;
        }
    }
}
