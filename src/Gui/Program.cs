using System;
using System.Windows;

namespace PCLDownloadFix.Gui
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Bootstrap.Init();
            var application = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };

            var disclaimer = new DisclaimerWindow();
            if (disclaimer.ShowDialog() != true)
            {
                application.Shutdown();
                return;
            }

            application.ShutdownMode = ShutdownMode.OnMainWindowClose;
            application.Run(new MainWindow(args != null && args.Length > 0 ? args[0] : null));
        }
    }
}
