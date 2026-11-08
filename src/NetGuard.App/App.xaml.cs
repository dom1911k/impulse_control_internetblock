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
        if (!WinEnv.IsElevatedAdmin())
        {
            MessageBox.Show("NetGuard needs administrator rights to change the firewall.\n\nRight-click NetGuard.exe → Run as administrator.",
                "NetGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(2);
            return;
        }
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "NetGuard", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
