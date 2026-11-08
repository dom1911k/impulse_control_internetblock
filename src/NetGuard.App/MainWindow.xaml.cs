using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetGuard.Core;
using NetGuard.Core.Config;
using NetGuard.Core.Hosts;
using NetGuard.Core.Logging;
using NetGuard.Core.Reporting;
using NetGuard.Core.State;
using NetGuard.Core.Updates;
using NetGuard.Windows;

namespace NetGuard.App;

public sealed record LogRow(string When, string What, string Details);

public partial class MainWindow : Window
{
    private readonly Engine _engine;
    private readonly string _actor = WinEnv.CurrentUser();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private NetGuardState _state = new();
    private bool _busy;
    private bool _ending;
    private int _tick;
    private ReleaseInfo? _latest;

    public MainWindow()
    {
        InitializeComponent();
        _engine = new Engine(new ComFirewall(), new AuthenticodeVerifier(), new NetGuardPaths(), HostsFile.DefaultPath);
        VersionText.Text = $"Installed version: {GitHubReleases.CurrentVersion}";
        _timer.Tick += OnTick;
        Loaded += OnLoaded;
    }

    // ================================================================ lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ReloadState();
        BuildProfileCards();
        LoadHistory();
        _timer.Start();
        Output.Text = "Welcome to NetGuard.\n\n" +
                      (File.Exists(_engine.Paths.ConfigFile)
                          ? "Pick a profile and start a test lock, or use Preview to see exactly what would change."
                          : "First time here? Open the Setup tab: Find apps → Create configuration.");

        // A test lock whose time is already up (e.g. NetGuard was closed): finish it now.
        if (_state.TestMode is { } tm && _state.ActiveProfile != null && tm.DeadlineUtc <= DateTimeOffset.UtcNow)
            _ = EndTestAsync(endedEarly: false);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (++_tick % 5 == 0 && !_busy) ReloadState(); // notices if the safety task restored internet

        if (_state.TestMode is { } tm && _state.ActiveProfile != null)
        {
            var left = tm.DeadlineUtc - DateTimeOffset.UtcNow;
            CountdownText.Text = left > TimeSpan.Zero ? left.ToString(@"mm\:ss") : "00:00";
            if (left <= TimeSpan.Zero && !_ending && !_busy) _ = EndTestAsync(endedEarly: false);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_state.TestMode is not { } tm || _state.ActiveProfile == null || _ending) return;
        var r = MessageBox.Show(this,
            $"A test lock is running until {tm.DeadlineUtc.ToLocalTime():T}.\n\n" +
            "Yes – restore normal internet now, then close\n" +
            "No – close anyway (the lock still ends by itself about 30 seconds after that time)\n" +
            "Cancel – keep NetGuard open",
            "NetGuard", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (r == MessageBoxResult.Yes)
        {
            try { TestMode.End(_engine, _actor, endedEarly: true); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "NetGuard", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }

    // ================================================================ state + banner

    private void ReloadState()
    {
        try { _state = _engine.LoadState(); }
        catch (Exception e) { StatusDetail.Text = "Could not read state: " + e.Message; return; }

        var profile = _state.ActiveProfile;
        var test = _state.TestMode;
        if (profile == null)
        {
            StatusTitle.Text = "Normal internet";
            StatusDetail.Text = File.Exists(_engine.Paths.ConfigFile) ? "No profile is active." : "NetGuard isn't set up yet – see the Setup tab.";
            Banner.Background = Brush("#2E3A46");
        }
        else
        {
            StatusTitle.Text = $"{profile} mode";
            StatusDetail.Text = test != null
                ? $"Test lock – normal internet comes back by itself at {test.DeadlineUtc.ToLocalTime():T}."
                : "Locked with no automatic end. Tools → Restore normal internet undoes it.";
            Banner.Background = Brush(BannerColor(profile));
        }
        CountdownPanel.Visibility = test != null && profile != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateCardStates();
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

    private readonly List<(string profile, Button start, TextBlock badge)> _cards = new();

    private void BuildProfileCards()
    {
        ProfileCards.Children.Clear();
        _cards.Clear();
        NetGuardConfig cfg;
        try
        {
            if (!File.Exists(_engine.Paths.ConfigFile))
            {
                NoConfigHint.Visibility = Visibility.Visible;
                ConfigStateText.Text = "No configuration yet. This saves the apps found in step 1 and protects NetGuard's folder so only administrators can change it.";
                CreateConfigButton.Content = "Create configuration";
                return;
            }
            cfg = _engine.LoadConfig();
        }
        catch (Exception e)
        {
            NoConfigHint.Visibility = Visibility.Visible;
            NoConfigHint.Text = "The configuration file can't be read: " + e.Message;
            return;
        }

        NoConfigHint.Visibility = Visibility.Collapsed;
        ConfigStateText.Text = $"Saved at {_engine.Paths.ConfigFile}. Re-creating it runs the app search again and overwrites the file.";
        CreateConfigButton.Content = "Re-create configuration…";

        foreach (var p in cfg.Profiles)
        {
            var stack = new StackPanel();
            stack.Children.Add(new Border { Height = 5, CornerRadius = new CornerRadius(3), Background = Brush(BannerColor(p.Name)), Margin = new Thickness(0, 0, 0, 12) });

            var titleRow = new DockPanel();
            var badge = new TextBlock
            {
                Text = "ACTIVE", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                Background = Brush(BannerColor(p.Name)), Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            DockPanel.SetDock(badge, Dock.Right);
            titleRow.Children.Add(badge);
            titleRow.Children.Add(new TextBlock { Text = p.Name, Style = (Style)FindResource("H2") });
            stack.Children.Add(titleRow);

            stack.Children.Add(new TextBlock { Text = Describe(p), Style = (Style)FindResource("Body"), MinHeight = 60 });

            var preview = new Button { Content = "Preview", Style = (Style)FindResource("Secondary") };
            preview.Click += async (_, _) => await PreviewAsync(p.Name);
            var start = new Button { Content = "Start test lock", Style = (Style)FindResource("Primary") };
            start.Click += async (_, _) => await StartTestAsync(p.Name);
            var buttons = new WrapPanel();
            buttons.Children.Add(start);
            buttons.Children.Add(preview);
            stack.Children.Add(buttons);

            ProfileCards.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = stack });
            _cards.Add((p.Name, start, badge));
        }
        UpdateCardStates();
    }

    private static string Describe(ProfileDefinition p)
    {
        if (p.Kind == ProfileKind.AllowAll)
            return "Everything is online except Steam. For Windows Update, Defender updates and installing things.";
        if (p.Programs.Count == 0)
            return "No internet at all. Only name lookups (DNS) and the clock sync stay on.";
        return "Online: " + string.Join(", ", p.Programs.Select(x => x.Name)) + ". Everything else is offline. Steam is always blocked.";
    }

    private void UpdateCardStates()
    {
        var locked = _state.ActiveProfile != null;
        foreach (var (profile, start, badge) in _cards)
        {
            var active = string.Equals(profile, _state.ActiveProfile, StringComparison.OrdinalIgnoreCase);
            badge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            start.IsEnabled = !locked;
            start.ToolTip = locked ? "Restore normal internet first." : null;
        }
    }

    // ================================================================ actions

    /// <summary>Run work off the UI thread and show its text in the Details box.</summary>
    private async Task<bool> RunAsync(string title, Func<string> work)
    {
        if (_busy) return false;
        _busy = true;
        Tabs.IsEnabled = false;
        RestoreNowButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        DetailsExpander.IsExpanded = true;
        Output.Text = title + " …";
        try
        {
            var text = await Task.Run(work);
            Output.Text = title + "\n\n" + text;
            return true;
        }
        catch (Exception e)
        {
            Output.Text = title + " – did not work\n\n" + e.Message;
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            Tabs.IsEnabled = true;
            RestoreNowButton.IsEnabled = true;
            _busy = false;
            ReloadState();
            LoadHistory();
        }
    }

    private Task PreviewAsync(string profile) => RunAsync($"Preview: {profile}", () =>
    {
        var cfg = _engine.LoadConfig();
        var d = _engine.BuildDesired(cfg, profile);
        var plan = _engine.PlanApply(cfg, d);
        return Report.Desired(d) + "\n" + Report.Plan(plan) + "\nThis was only a preview – nothing was changed.";
    });

    private async Task StartTestAsync(string profile)
    {
        var minutes = int.Parse((string)((ComboBoxItem)MinutesBox.SelectedItem).Tag);
        var cfg = _engine.LoadConfig();
        var p = cfg.FindProfile(profile)!;
        var until = DateTime.Now.AddMinutes(minutes);
        var what = p.Kind == ProfileKind.AllowAll
            ? "everything except Steam can use the internet"
            : p.Programs.Count == 0
                ? "nothing can use the internet"
                : "only " + string.Join(", ", p.Programs.Select(x => x.Name)) + " can use the internet";

        var ok = MessageBox.Show(this,
            $"Start a {minutes}-minute {p.Name} test lock?\n\nUntil about {until:t}, {what}.\n\n" +
            "It ends by itself. You can also end it early with \"Restore internet now\".",
            "Start test lock", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;

        await RunAsync($"Test lock: {p.Name} for {minutes} min", () =>
        {
            var (info, result) = TestMode.Start(_engine, cfg, profile, minutes, _actor);
            return $"Safety task armed: normal internet returns at {info.DeadlineUtc.ToLocalTime():T} even if NetGuard is closed.\n\n" +
                   Report.Result(result);
        });
    }

    private async void OnRestoreNow(object sender, RoutedEventArgs e) => await EndTestAsync(endedEarly: true);

    private async Task EndTestAsync(bool endedEarly)
    {
        if (_ending) return;
        _ending = true;
        try
        {
            await RunAsync(endedEarly ? "Restoring normal internet" : "Test lock finished – restoring normal internet",
                () => Report.Result(TestMode.End(_engine, _actor, endedEarly)) + "\nNormal internet is back.");
        }
        finally { _ending = false; }
    }

    // ---------- setup

    private async void OnDetect(object sender, RoutedEventArgs e) =>
        await RunAsync("Find apps", () => Report.Detected(AppDetector.Detect()));

    private async void OnInit(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_engine.Paths.ConfigFile) &&
            MessageBox.Show(this, "Search for apps again and overwrite the current configuration?", "NetGuard",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        await RunAsync("Create configuration", () =>
        {
            var d = AppDetector.Detect();
            var cfg = DefaultConfig.Create(d);
            DataDirSecurity.Secure(_engine.Paths);
            cfg.Save(_engine.Paths.ConfigFile);
            _engine.Log.Append(LogTypes.ConfigCreated, new { detected = d }, _actor);
            return Report.Detected(d) + $"\nConfiguration saved to {_engine.Paths.ConfigFile}.\n" +
                   "Only SYSTEM and administrators can change NetGuard's folder now.\n\nNext: step 3, then the Profiles tab.";
        });
        BuildProfileCards();
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_engine.Paths.DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_engine.Paths.DataDir}\"") { UseShellExecute = true });
    }

    private async void OnPrograms(object sender, RoutedEventArgs e) => await RunAsync("Allowed files per profile", () =>
    {
        var cfg = _engine.LoadConfig();
        var sb = new StringBuilder();
        foreach (var p in cfg.Profiles) sb.AppendLine(Report.Desired(_engine.BuildDesired(cfg, p.Name)));
        return sb.ToString();
    });

    // ---------- history

    private void OnRefreshHistory(object sender, RoutedEventArgs e) => LoadHistory();

    private void LoadHistory()
    {
        try
        {
            var rows = _engine.Log.ReadAll().Reverse().Take(500).Select(o =>
                new LogRow(
                    DateTimeOffset.TryParse((string?)o["ts"], out var t) ? t.ToLocalTime().ToString("ddd dd.MM. HH:mm:ss") : "?",
                    Report.EventName((string?)o["type"]),
                    Report.LogSummary(o))).ToList();
            HistoryList.ItemsSource = rows;
            HistoryCount.Text = rows.Count == 0 ? "Nothing logged yet." : $"{rows.Count} entries (newest first)";
        }
        catch (Exception e) { HistoryCount.Text = "Could not read the log: " + e.Message; }
    }

    // ---------- tools

    private async void OnRecover(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Restore normal internet now?\n\nThis removes every NetGuard rule and switches the firewall back to normal.",
                "Restore normal internet", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await RunAsync("Restore normal internet", () =>
        {
            var (plan, result) = _engine.Recover("manual recovery from the NetGuard window", _actor);
            try { DeadManSwitch.Disarm(); } catch { }
            return Report.Plan(plan) + "\n" + Report.Result(result) + (result.Success ? "\nNormal internet is back." : "");
        });
    }

    private async void OnStatus(object sender, RoutedEventArgs e) =>
        await RunAsync("Status", () => StatusReader.Read(_engine).Describe());

    private async void OnAuditOn(object sender, RoutedEventArgs e) =>
        await RunAsync("Start recording blocked connections", () =>
        {
            BlockedConnections.SetAuditing(true);
            return "Windows now records blocked connections.\nReproduce the problem, then click \"Show blocked\". Click \"Stop recording\" when you're done.";
        });

    private async void OnAuditOff(object sender, RoutedEventArgs e) =>
        await RunAsync("Stop recording blocked connections", () => { BlockedConnections.SetAuditing(false); return "Recording stopped."; });

    private async void OnShowBlocked(object sender, RoutedEventArgs e) => await RunAsync("Blocked programs (last 10 minutes)", () =>
    {
        var apps = BlockedConnections.Query(TimeSpan.FromMinutes(10));
        if (apps.Count == 0) return "None found. Is recording on? Click \"Start recording\" and try again.";
        var sb = new StringBuilder();
        foreach (var a in apps)
            sb.AppendLine($"{a.Count,6}x  {a.Application}\n        last {a.LastSeen:T} → {string.Join(", ", a.Destinations)}");
        return sb.ToString();
    });

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        InstallUpdateButton.IsEnabled = false;
        await RunAsync("Check for updates", () =>
        {
            var latest = Updater.CheckLatestAsync().GetAwaiter().GetResult();
            _latest = latest;
            var current = GitHubReleases.CurrentVersion;
            if (latest.Version <= current) return $"You have the newest version ({current}).";
            return $"Version {latest.Version} is available (you have {current}).\n\nWhat's new:\n{latest.Notes}";
        });
        if (_latest != null && _latest.Version > GitHubReleases.CurrentVersion)
        {
            InstallUpdateButton.IsEnabled = true;
            VersionText.Text = $"Installed version: {GitHubReleases.CurrentVersion} – version {_latest.Version} is available.";
        }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_latest == null) return;
        if (_state.TestMode != null && _state.ActiveProfile != null)
        {
            MessageBox.Show(this, "Please end the test lock before updating.", "NetGuard", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"Install version {_latest.Version}? NetGuard restarts afterwards.", "Update",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var release = _latest;
        var from = GitHubReleases.CurrentVersion;
        var ok = await RunAsync($"Installing version {release.Version}", () =>
        {
            var files = Updater.InstallAsync(release).GetAwaiter().GetResult();
            _engine.Log.Append(LogTypes.Updated, new { from = from.ToString(), to = release.Version.ToString(), release = release.HtmlUrl, files }, _actor);
            return "Installed: " + string.Join(", ", files);
        });
        if (!ok) return;

        MessageBox.Show(this, $"NetGuard was updated to {release.Version} and will now restart.", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
        var exe = Environment.ProcessPath;
        if (exe != null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }
}
