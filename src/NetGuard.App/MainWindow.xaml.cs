using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetGuard.Core.Ipc;
using NetGuard.Core.Updates;
using NetGuard.Windows;

namespace NetGuard.App;

public sealed record LogRow(string When, string What, string Details);

/// <summary>
/// The NetGuard window. It runs with normal rights and never touches the firewall itself:
/// every action is a request to the NetGuard service, which decides whether it's allowed.
/// </summary>
public partial class MainWindow : Window
{
    private readonly GuardClient _client = new();
    private readonly StatusMonitor _monitor;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private StatusDto? _status;
    private bool _serviceDown;
    private string _serviceProblem = "";
    private bool _busy;
    private bool _allowClose;
    private ReleaseInfo? _latest;
    private string _cardsSignature = "";

    public MainWindow(StatusMonitor monitor)
    {
        _monitor = monitor;
        InitializeComponent();
        VersionText.Text = $"Installed version: {GitHubReleases.CurrentVersion}";
        _timer.Tick += OnTick;
        Loaded += OnLoaded;
        _monitor.Changed += OnStatusChanged;
        Programs = new ProgramsPanel(this, _client);
        ProgramsTab.Content = Programs;
        Tabs.SelectionChanged += async (_, e) =>
        {
            if (e.Source == Tabs && Tabs.SelectedItem == ProgramsTab) await Programs.RefreshAsync();
            if (e.Source == Tabs && Tabs.SelectedItem == HistoryTab) await LoadHistoryAsync();
        };
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true; // closing the window keeps NetGuard in the tray
            Hide();
        };
    }

    public ProgramsPanel Programs { get; }

    public void AllowClose() { _allowClose = true; Close(); }

    public void OpenWizard()
    {
        if (!IsVisible) Show();
        var w = new WizardWindow(this, _client, _monitor);
        w.ShowDialog();
        _ = _monitor.RefreshAsync();
    }

    private void OnRunWizard(object sender, RoutedEventArgs e) => OpenWizard();

    private void OnStatusChanged()
    {
        _status = _monitor.Status;
        _serviceDown = _monitor.ServiceDown;
        _serviceProblem = _monitor.Problem;
        ApplyStatus();
    }

    // ================================================================ lifecycle / status

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        OnStatusChanged();
        await LoadHistoryAsync();
        _timer.Start();
        Output.Text = _serviceDown
            ? "The NetGuard protection service isn't running.\n\nInstall or repair NetGuard with NetGuardSetup.exe, or Tools → Start protection service."
            : _status?.ConfigExists == false || _status?.RecoveryCodeExists == false
                ? "Welcome to NetGuard.\n\nFirst time here? Setup → Run setup wizard."
                : "Welcome to NetGuard.\n\nSwitch to a profile, start a test lock, or use Preview to see exactly what would change.";
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_status?.TestDeadlineUtc is { } deadline)
        {
            var left = deadline - DateTimeOffset.UtcNow;
            CountdownText.Text = left > TimeSpan.Zero ? left.ToString(@"mm\:ss") : "00:00";
        }
    }

    private Task RefreshStatusAsync() => _monitor.RefreshAsync();

    private void ApplyStatus()
    {
        if (_serviceDown || _status == null)
        {
            var running = SafeServiceState().StartsWith("running");
            StatusTitle.Text = running ? "Can't reach the protection service" : "Protection service isn't running";
            StatusDetail.Text = running
                ? $"{_serviceProblem} Please send a screenshot of this to whoever helps you with NetGuard."
                : "NetGuard can't enforce or repair anything right now. Run NetGuardSetup.exe to install or repair it.";
            Banner.Background = Brush("#7A2E2E");
            CountdownPanel.Visibility = Visibility.Collapsed;
            ServiceText.Text = $"Service: {SafeServiceState()}.";
            StartServiceButton.Visibility = Visibility.Visible;
            BuildProfileCards(null);
            return;
        }

        ServiceText.Text = $"Running (version {_status.ServiceVersion}), self-protection {(_status.SelfProtectionActive ? "on" : "on standby until a lock starts")}. " +
                           $"Last check {_status.LastCheckUtc?.ToLocalTime():T}." +
                           (_status.LastTamperUtc is { } t ? $"\nLast repair of a change: {t.ToLocalTime():g}." : "");
        RecoveryStateText.Text = _status.RecoveryCodeExists
            ? $"✓ You have a recovery code (created {_status.RecoveryCodeCreatedUtc?.ToLocalTime():g})."
            : "No recovery code yet – locks can't be started until you create one.";
        RecoveryButton.Content = _status.RecoveryCodeExists ? "Replace recovery code…" : "Create recovery code";
        RecoveryButton.IsEnabled = _status.ActiveProfile == null;
        RecoveryButton.ToolTip = _status.ActiveProfile == null ? null : "Only possible with normal internet.";
        StartServiceButton.Visibility = Visibility.Collapsed;

        var profile = _status.ActiveProfile;
        if (profile == null)
        {
            StatusTitle.Text = "Normal internet";
            StatusDetail.Text = !_status.ConfigExists || !_status.RecoveryCodeExists
                ? "NetGuard isn't fully set up yet – see the Setup tab."
                : "No profile is active.";
            Banner.Background = Brush("#2E3A46");
            ProfileCards.Opacity = 1;
        }
        else
        {
            StatusTitle.Text = $"{profile} mode";
            StatusDetail.Text = _status.TestDeadlineUtc is { } d
                ? $"Test lock – normal internet comes back by itself at {d.ToLocalTime():T}."
                : $"Active since {_status.ActiveSinceUtc?.ToLocalTime():g}. NetGuard keeps it in place and repairs any change.";
            Banner.Background = Brush(Theme.ProfileColor(profile));
        }
        CountdownPanel.Visibility = _status.TestDeadlineUtc != null ? Visibility.Visible : Visibility.Collapsed;
        BuildProfileCards(_status);
    }

    private static string SafeServiceState()
    {
        try { return ServiceControl.Describe(); } catch { return "unknown"; }
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    // ================================================================ profile cards

    private void BuildProfileCards(StatusDto? status)
    {
        // Only rebuild when something visible changed (keeps buttons from flickering every 3 s).
        var sig = status == null ? "down" :
            string.Join("|", status.Profiles.Select(p => $"{p.Name}{p.IsActive}{p.SwitchAllowed}{p.TestAllowed}{p.SwitchHint}")) + status.ConfigExists;
        if (sig == _cardsSignature) return;
        _cardsSignature = sig;

        ProfileCards.Children.Clear();
        if (status == null) { NoConfigHint.Visibility = Visibility.Collapsed; return; }
        NoConfigHint.Visibility = status.ConfigExists ? Visibility.Collapsed : Visibility.Visible;

        foreach (var p in status.Profiles)
        {
            var stack = new StackPanel();
            stack.Children.Add(new Border { Height = 5, CornerRadius = new CornerRadius(3), Background = Brush(Theme.ProfileColor(p.Name)), Margin = new Thickness(0, 0, 0, 12) });

            var titleRow = new DockPanel();
            if (p.IsActive)
            {
                var badge = new TextBlock
                {
                    Text = "ACTIVE", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                    Background = Brush(Theme.ProfileColor(p.Name)), Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(badge, Dock.Right);
                titleRow.Children.Add(badge);
            }
            titleRow.Children.Add(new TextBlock { Text = p.Name, Style = (Style)FindResource("H2") });
            stack.Children.Add(titleRow);
            stack.Children.Add(new TextBlock { Text = p.Description, Style = (Style)FindResource("Body"), MinHeight = 60 });

            var name = p.Name;
            var switchBtn = new Button
            {
                Content = p.IsActive ? "Active" : $"Switch to {p.Name}",
                Style = (Style)FindResource("Primary"),
                IsEnabled = !p.IsActive && p.SwitchAllowed,
            };
            switchBtn.Click += async (_, _) => await SwitchAsync(name);
            var testBtn = new Button { Content = "Test lock", Style = (Style)FindResource("Secondary"), IsEnabled = p.TestAllowed, ToolTip = p.TestAllowed ? null : p.TestHint };
            testBtn.Click += async (_, _) => await StartTestAsync(name);
            var previewBtn = new Button { Content = "Preview", Style = (Style)FindResource("Secondary") };
            previewBtn.Click += async (_, _) => await PreviewAsync(name);

            var buttons = new WrapPanel();
            buttons.Children.Add(switchBtn);
            buttons.Children.Add(testBtn);
            buttons.Children.Add(previewBtn);
            stack.Children.Add(buttons);

            if (!p.IsActive && !p.SwitchAllowed)
                stack.Children.Add(new TextBlock { Text = p.SwitchHint, Style = (Style)FindResource("Body"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });

            ProfileCards.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = stack });
        }
    }

    // ================================================================ running requests

    /// <summary>Send a request to the service and show its answer in the Details box.</summary>
    private async Task<IpcResponse?> RunAsync(string title, IpcRequest request)
    {
        if (_busy) return null;
        _busy = true;
        Tabs.IsEnabled = false;
        RestoreNowButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        DetailsExpander.IsExpanded = true;
        Output.Text = title + " …";
        try
        {
            var r = await _client.SendAsync(request);
            Output.Text = r.Ok ? $"{title}\n\n{r.Text}" : $"{title} – not done\n\n{r.Error}";
            return r;
        }
        catch (Exception e)
        {
            Output.Text = $"{title} – did not work\n\n{e.Message}";
            return null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            Tabs.IsEnabled = true;
            RestoreNowButton.IsEnabled = true;
            _busy = false;
            _cardsSignature = "";
            await RefreshStatusAsync();
            await LoadHistoryAsync();
        }
    }

    private Task PreviewAsync(string profile) =>
        RunAsync($"Preview: {profile}", new IpcRequest { Command = IpcCommands.Preview, Profile = profile });

    private async Task SwitchAsync(string profile)
    {
        var p = _status?.Profiles.FirstOrDefault(x => x.Name == profile);
        if (p == null) return;
        var msg = $"Switch to {p.Name} now?\n\n{p.Description}\n\n" +
                  "This lock has no end time. Stricter profiles are always one click away. Going back to something looser " +
                  "needs the unlock process (milestone 4) – until then only the emergency restore with your recovery code.";
        if (MessageBox.Show(this, msg, "Switch profile", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunAsync($"Switch to {p.Name}", new IpcRequest { Command = IpcCommands.Switch, Profile = profile });
    }

    private async Task StartTestAsync(string profile)
    {
        var p = _status?.Profiles.FirstOrDefault(x => x.Name == profile);
        if (p == null) return;
        var minutes = int.Parse((string)((ComboBoxItem)MinutesBox.SelectedItem).Tag);
        var until = DateTime.Now.AddMinutes(minutes);
        var what = p.Kind == "AllowAll"
            ? "everything except Steam can use the internet"
            : p.Programs.Count == 0 ? "nothing can use the internet" : "only " + string.Join(", ", p.Programs) + " can use the internet";
        if (MessageBox.Show(this,
                $"Start a {minutes}-minute {p.Name} test lock?\n\nUntil about {until:t}, {what}.\n\n" +
                "It ends by itself. You can also end it early with \"Restore internet now\".",
                "Start test lock", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await RunAsync($"Test lock: {p.Name} for {minutes} min", new IpcRequest { Command = IpcCommands.TestStart, Profile = profile, Minutes = minutes });
    }

    private async void OnRestoreNow(object sender, RoutedEventArgs e) =>
        await RunAsync("Ending the test lock", new IpcRequest { Command = IpcCommands.TestEnd });

    // ---------- setup

    // ---------- history

    private async void OnRefreshHistory(object sender, RoutedEventArgs e) => await LoadHistoryAsync();

    private async Task LoadHistoryAsync()
    {
        try
        {
            var r = await _client.SendAsync(new IpcRequest { Command = IpcCommands.History }, TimeSpan.FromSeconds(15));
            var rows = (r.History ?? new()).Select(h => new LogRow(
                h.Ts.ToLocalTime().ToString("ddd dd.MM. HH:mm:ss"), h.What, h.Details)).ToList();
            HistoryList.ItemsSource = rows;
            HistoryCount.Text = rows.Count == 0 ? "Nothing logged yet." : $"{rows.Count} entries (newest first)";
        }
        catch (GuardClient.ServiceUnavailableException) { HistoryCount.Text = "The service isn't running, so the history can't be shown."; }
        catch (Exception e) { HistoryCount.Text = "Could not load the history: " + e.Message; }
    }

    // ---------- tools

    private async void OnRecover(object sender, RoutedEventArgs e)
    {
        string? code = null;
        if (_status?.ActiveProfile != null)
        {
            code = Dialogs.AskRecoveryCode(this,
                $"Emergency restore ends the {_status.ActiveProfile} lock now and brings back normal internet. It will be logged.");
            if (code == null) return;
        }
        else if (MessageBox.Show(this, "No lock is active. Run the restore anyway (removes any NetGuard rules left over)?",
                     "Emergency restore", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await RunAsync("Emergency restore", new IpcRequest { Command = IpcCommands.Recover, Code = code });
    }

    private async void OnCreateRecoveryCode(object sender, RoutedEventArgs e)
    {
        if (_status?.RecoveryCodeExists == true &&
            MessageBox.Show(this, "Create a new recovery code? The old one stops working.", "Recovery code",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        IpcResponse r;
        try { r = await _client.SendAsync(new IpcRequest { Command = IpcCommands.RecoveryCreate }); }
        catch (Exception ex) { Output.Text = "Could not create a recovery code: " + ex.Message; return; }
        if (!r.Ok || r.Text == null) { Output.Text = "Could not create a recovery code: " + r.Error; return; }
        Dialogs.ShowNewRecoveryCode(this, r.Text);
        Output.Text = "Recovery code created. Keep the paper safe.";
        _cardsSignature = "";
        await RefreshStatusAsync();
        await LoadHistoryAsync();
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        string? code = null;
        if (_status?.ActiveProfile != null)
        {
            code = Dialogs.AskRecoveryCode(this, "A lock is active. Uninstalling during a lock needs your recovery code.");
            if (code == null) return;
        }
        else if (MessageBox.Show(this, "Uninstall NetGuard?\n\nNormal internet is restored and NetGuard's protection is switched off. " +
                                       "Then the Windows uninstaller opens (Windows asks for permission).",
                     "Uninstall NetGuard", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var r = await RunAsync("Preparing to uninstall", new IpcRequest { Command = IpcCommands.PrepareUninstall, Code = code });
        if (r?.Ok != true) return;
        await Task.Delay(3000); // let the service stop
        var uninstaller = System.IO.Path.Combine(AppContext.BaseDirectory, "unins000.exe");
        if (!System.IO.File.Exists(uninstaller)) uninstaller = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Environment.ProcessPath!)!, "unins000.exe");
        try
        {
            Process.Start(new ProcessStartInfo(uninstaller) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Output.Text += "\n\nCould not start the uninstaller (" + ex.Message + "). Use Windows Settings → Apps → NetGuard → Uninstall.";
        }
    }

    private async void OnStatus(object sender, RoutedEventArgs e) =>
        await RunAsync("Status", new IpcRequest { Command = IpcCommands.StatusText });

    private async void OnAuditOn(object sender, RoutedEventArgs e) =>
        await RunAsync("Start recording blocked connections", new IpcRequest { Command = IpcCommands.Audit, On = true });

    private async void OnAuditOff(object sender, RoutedEventArgs e) =>
        await RunAsync("Stop recording blocked connections", new IpcRequest { Command = IpcCommands.Audit, On = false });

    private async void OnShowBlocked(object sender, RoutedEventArgs e) =>
        await RunAsync("Blocked programs (last 10 minutes)", new IpcRequest { Command = IpcCommands.Blocked });

    private void OnStartService(object sender, RoutedEventArgs e)
    {
        var ngctl = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Environment.ProcessPath!)!, "ngctl.exe");
        try
        {
            var p = Process.Start(new ProcessStartInfo(ngctl, "service on") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(30_000);
            Output.Text = "Asked Windows to start the NetGuard protection service.";
        }
        catch (Win32Exception) { Output.Text = "Cancelled."; }
        catch (Exception ex) { Output.Text = "Could not start the service: " + ex.Message; }
        _ = RefreshStatusAsync();
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        InstallUpdateButton.IsEnabled = false;
        Output.Text = "Checking for updates …";
        try
        {
            _latest = await Updater.CheckLatestAsync();
            var current = GitHubReleases.CurrentVersion;
            if (_latest.Version <= current)
                Output.Text = $"You have the newest version ({current}).";
            else
            {
                Output.Text = $"Version {_latest.Version} is available (you have {current}).\n\nWhat's new:\n{_latest.Notes}";
                InstallUpdateButton.IsEnabled = true;
                VersionText.Text = $"Installed version: {current} – version {_latest.Version} is available.";
            }
        }
        catch (Exception ex) { Output.Text = "Could not check for updates: " + ex.Message; }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_latest == null) return;
        if (MessageBox.Show(this, $"Install version {_latest.Version}?\n\nThe NetGuard service downloads and installs it. NetGuard closes; open it again in about a minute.",
                "Update", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        InstallUpdateButton.IsEnabled = false;
        var r = await RunAsync("Installing update", new IpcRequest { Command = IpcCommands.Update });
        if (r?.Ok == true && r.Text?.StartsWith("Installing") == true)
        {
            MessageBox.Show(this, r.Text, "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Shutdown();
        }
        else InstallUpdateButton.IsEnabled = true;
    }
}
