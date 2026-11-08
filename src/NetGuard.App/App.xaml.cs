using System.Windows;
using System.Windows.Threading;
using NetGuard.Windows;

namespace NetGuard.App;

/// <summary>
/// NetGuard.exe: lives in the tray (started with Windows using --tray) and opens the main window on demand.
/// Only one copy runs; starting it again just brings the window to the front.
/// </summary>
public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _showSignal;
    private StatusMonitor? _monitor;
    private MainWindow? _main;
    private TrayIcon? _tray;
    private bool _wizardOffered;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _single = new Mutex(true, @"Local\NetGuardApp", out var first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(@"Local\NetGuardAppShow").Set(); } catch { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NetGuardAppShow");
        new Thread(() =>
        {
            while (_showSignal.WaitOne()) Dispatcher.Invoke(ShowMain);
        }) { IsBackground = true }.Start();

        Updater.CleanupOldFiles();
        base.OnStartup(e);

        _monitor = new StatusMonitor();
        _main = new MainWindow(_monitor);
        _tray = new TrayIcon(_monitor, ShowMain, ExitApp);
        var trayOnly = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        _monitor.Changed += () => OfferWizard(trayOnly);
        _monitor.Start();
        _ = _monitor.RefreshAsync();
        if (!trayOnly) ShowMain();
    }

    /// <summary>First run: open the setup wizard once NetGuard knows it isn't set up yet.</summary>
    private void OfferWizard(bool trayOnly)
    {
        if (_wizardOffered || _monitor?.Status is not { } s) return;
        _wizardOffered = true;
        if (s.ConfigExists && s.RecoveryCodeExists) return;
        if (trayOnly) ShowMain();
        _main!.OpenWizard();
    }

    public void ShowMain()
    {
        if (_main == null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void ExitApp()
    {
        _tray?.Dispose();
        _main?.AllowClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "NetGuard", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
