using System.Windows;
using System.Windows.Threading;
using NetGuard.Windows;

namespace NetGuard.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        Updater.CleanupOldFiles();
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "NetGuard", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
