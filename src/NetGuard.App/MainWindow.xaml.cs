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
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private StatusDto? _status;
    private bool _serviceDown;
    private bool _busy;
    private bool _refreshing;
    private int _tick;
    private ReleaseInfo? _latest;
    private string _cardsSignature = "";

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Installed version: {GitHubReleases.CurrentVersion}";
        _timer.Tick += OnTick;
        Loaded += OnLoaded;
    }

    // ================================================================ lifecycle / status

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
        await LoadHistoryAsync();
        _timer.Start();
        Output.Text = _serviceDown
            ? "The NetGuard protection service isn't running.\n\nInstall or repair NetGuard with NetGuardSetup.exe, or (as administrator) Tools → Start protection service."
            : _status?.ConfigExists == false
                ? "Welcome to NetGuard.\n\nFirst time here? Tools → Restart as administrator, then Setup: Find apps → Create configuration."
                : "Welcome to NetGuard.\n\nSwitch to a profile, start a test lock, or use Preview to see exactly what would change.";
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_status?.TestDeadlineUtc is { } deadline)
        {
            var left = deadline - DateTimeOffset.UtcNow;
            CountdownText.Text = left > TimeSpan.Zero ? left.ToString(@"mm\:ss") : "00:00";
        }
        if (++_tick % 3 == 0 && !_busy) await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var r = await _client.SendAsync(new IpcRequest { Command = IpcCommands.Status }, TimeSpan.FromSeconds(10));
            _status = r.Status;
            _serviceDown = false;
        }
        catch (GuardClient.ServiceUnavailableException)
        {
            _status = null;
            _serviceDown = true;
        }
        catch (Exception ex)
        {
            StatusDetail.Text = "Could not ask the service: " + ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
        ApplyStatus();
    }

    private void ApplyStatus()
    {
        var elevated = WinEnv.IsElevatedAdmin();
        AccountText.Text = _status != null
            ? $"Signed in as {_status.CallerName} – {(_status.CallerIsAdmin ? "administrator" : "normal rights")}."
            : $"Signed in as {WinEnv.CurrentUser()}{(elevated ? " – administrator" : "")}.";
        ElevateButton.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;

        if (_serviceDown || _status == null)
        {
            StatusTitle.Text = "Protection service isn't running";
            StatusDetail.Text = "NetGuard can't enforce or repair anything right now. Run NetGuardSetup.exe to install or repair it.";
            Banner.Background = Brush("#7A2E2E");
            CountdownPanel.Visibility = Visibility.Collapsed;
            ServiceText.Text = $"Service: {SafeServiceState()}.";
            StartServiceButton.Visibility = Visibility.Visible;
            BuildProfileCards(null);
            return;
        }

        ServiceText.Text = $"Running (version {_status.ServiceVersion}). Last check {_status.LastCheckUtc?.ToLocalTime():T}." +
                           (_status.LastTamperUtc is { } t ? $"\nLast repair of a change: {t.ToLocalTime():g}." : "");
        StartServiceButton.Visibility = Visibility.Collapsed;

        var profile = _status.ActiveProfile;
        if (profile == null)
        {
            StatusTitle.Text = "Normal internet";
            StatusDetail.Text = _status.ConfigExists ? "No profile is active." : "NetGuard isn't set up yet – an administrator needs to run Setup.";
            Banner.Background = Brush("#2E3A46");
        }
        else
        {
            StatusTitle.Text = $"{profile} mode";
            StatusDetail.Text = _status.TestDeadlineUtc is { } d
                ? $"Test lock – normal internet comes back by itself at {d.ToLocalTime():T}."
                : $"Active since {_status.ActiveSinceUtc?.ToLocalTime():g}. NetGuard keeps it in place and repairs any change.";
            Banner.Background = Brush(BannerColor(profile));
        }
        CountdownPanel.Visibility = _status.TestDeadlineUtc != null ? Visibility.Visible : Visibility.Collapsed;
        BuildProfileCards(_status);
    }

    private static string SafeServiceState()
    {
        try { return ServiceControl.Describe(); } catch { return "unknown"; }
    }

    private static string BannerColor(string profile) => profile.ToLowerInvariant() switch
    {
        "offline" => "#3D4B5C",
        "wow" => "#1F4E79",
        "maintenance" => "#8A5A12",
        _ => "#2E7D6B",
    };

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    // ================================================================ profile cards

    private void BuildProfileCards(StatusDto? status)
    {
        // Only rebuild when something visible changed (keeps buttons from flickering every 3 s).
        var sig = status == null ? "down" :
            string.Join("|", status.Profiles.Select(p => $"{p.Name}{p.IsActive}{p.SwitchAllowed}{p.TestAllowed}")) + status.ConfigExists;
        if (sig == _cardsSignature) return;
        _cardsSignature = sig;

        ProfileCards.Children.Clear();
        if (status == null) { NoConfigHint.Visibility = Visibility.Collapsed; return; }
        NoConfigHint.Visibility = status.ConfigExists ? Visibility.Collapsed : Visibility.Visible;
        ConfigStateText.Text = status.ConfigExists
            ? "A configuration exists. Re-creating it runs the app search again and overwrites it."
            : "No configuration yet. This saves the apps found in step 1 and protects NetGuard's folder so only administrators can change it.";
        CreateConfigButton.Content = status.ConfigExists ? "Re-create configuration…" : "Create configuration";

        foreach (var p in status.Profiles)
        {
            var stack = new StackPanel();
            stack.Children.Add(new Border { Height = 5, CornerRadius = new CornerRadius(3), Background = Brush(BannerColor(p.Name)), Margin = new Thickness(0, 0, 0, 12) });

            var titleRow = new DockPanel();
            if (p.IsActive)
            {
                var badge = new TextBlock
                {
                    Text = "ACTIVE", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                    Background = Brush(BannerColor(p.Name)), Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center,
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
        var looser = p.SwitchHint.StartsWith("looser", StringComparison.OrdinalIgnoreCase);
        var msg = $"Switch to {p.Name} now?\n\n{p.Description}\n\n" +
                  (looser
                      ? "This is looser than the current profile. It's allowed because you're an administrator."
                      : "This lock has no end time. Going back to something looser will need an administrator " +
                        "(the unlock process comes in milestone 4). Stricter profiles are always one click away.");
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

    private async void OnDetect(object sender, RoutedEventArgs e) =>
        await RunAsync("Find apps", new IpcRequest { Command = IpcCommands.Detect });

    private async void OnInit(object sender, RoutedEventArgs e)
    {
        if (_status?.ConfigExists == true &&
            MessageBox.Show(this, "Search for apps again and overwrite the current configuration?", "NetGuard",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await RunAsync("Create configuration", new IpcRequest { Command = IpcCommands.Init });
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        var dir = new NetGuard.Core.NetGuardPaths().DataDir;
        if (!System.IO.Directory.Exists(dir)) { Output.Text = "The data folder doesn't exist yet."; return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private async void OnPrograms(object sender, RoutedEventArgs e) =>
        await RunAsync("Allowed files per profile", new IpcRequest { Command = IpcCommands.Programs });

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
        if (MessageBox.Show(this, "Restore normal internet now?\n\nThis removes every NetGuard rule and switches the firewall back to normal. Administrators only.",
                "Restore normal internet", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await RunAsync("Restore normal internet", new IpcRequest { Command = IpcCommands.Recover });
    }

    private async void OnStatus(object sender, RoutedEventArgs e) =>
        await RunAsync("Status", new IpcRequest { Command = IpcCommands.StatusText });

    private async void OnAuditOn(object sender, RoutedEventArgs e) =>
        await RunAsync("Start recording blocked connections", new IpcRequest { Command = IpcCommands.Audit, On = true });

    private async void OnAuditOff(object sender, RoutedEventArgs e) =>
        await RunAsync("Stop recording blocked connections", new IpcRequest { Command = IpcCommands.Audit, On = false });

    private async void OnShowBlocked(object sender, RoutedEventArgs e) =>
        await RunAsync("Blocked programs (last 10 minutes)", new IpcRequest { Command = IpcCommands.Blocked });

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Win32Exception) { /* the user cancelled the Windows prompt */ }
    }

    private void OnStartService(object sender, RoutedEventArgs e)
    {
        if (!WinEnv.IsElevatedAdmin())
        {
            Output.Text = "Starting the service needs an administrator: Tools → Restart as administrator, then try again.";
            return;
        }
        try
        {
            ServiceControl.EnableAndStart();
            Output.Text = "The NetGuard protection service is running again and starts automatically with Windows.";
        }
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
        if (MessageBox.Show(this, $"Install version {_latest.Version}?\n\nWindows will ask for administrator permission. NetGuard closes during the update and opens again afterwards.",
                "Update", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        InstallUpdateButton.IsEnabled = false;
        Output.Text = "Downloading the installer …";
        try
        {
            var path = await Updater.DownloadInstallerAsync(_latest, m => Dispatcher.Invoke(() => Output.Text += "\n" + m));
            Updater.RunInstaller(path, silent: true);
            Application.Current.Shutdown();
        }
        catch (Win32Exception) { Output.Text += "\nCancelled."; InstallUpdateButton.IsEnabled = true; }
        catch (Exception ex) { Output.Text += "\nThe update did not work: " + ex.Message; InstallUpdateButton.IsEnabled = true; }
    }
}
