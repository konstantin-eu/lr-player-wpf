
using System;
using System.Windows;
using System.Windows.Media;

namespace SubtitleVideoPlayerWpf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Handle unhandled exceptions
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                MessageBox.Show($"An unhandled exception occurred: {ex?.Message}",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            // Set app-wide rendering options
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
        }
    }
}
