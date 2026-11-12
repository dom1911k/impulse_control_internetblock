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
    private readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private StatusDto? _status;
    private bool _serviceDown;
    private string _serviceProblem = "";
    private bool _busy;
    private bool _allowClose;
    private ReleaseInfo? _latest;
    private string _cardsSignature = "";
    private DateTime _statsLoaded = DateTime.MinValue;

    public ProgramsPanel Programs { get; }
    public PlayTimePanel PlayTime { get; }
    public SummaryPanel Summary { get; }
    public StatsPanel Stats { get; }

    public MainWindow(StatusMonitor monitor)
    {
        _monitor = monitor;
        InitializeComponent();
        VersionText.Text = $"Installed version: {GitHubReleases.CurrentVersion}";

        Programs = new ProgramsPanel(this, _client);
        ProgramsTab.Content = Programs;
        PlayTime = new PlayTimePanel(this, _client, () => _status);
        PlayTab.Content = PlayTime;
        Summary = new SummaryPanel(_client);
        SummaryHost.Content = Summary;
        Stats = new StatsPanel(_client);
        StatsHost.Content = Stats;

        // The passage has to be typed: no pasting, no dropping.
        DataObject.AddPastingHandler(PassageInput, (_, e) => e.CancelCommand());
        PassageInput.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, (_, _) => { }, (_, e) => { e.CanExecute = false; e.Handled = true; }));

        _timer.Tick += OnTick;
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); MessageBar.Visibility = Visibility.Collapsed; };
        _monitor.Changed += OnStatusChanged;
        Loaded += OnLoaded;

        Tabs.SelectionChanged += async (_, e) =>
        {
            if (e.Source != Tabs) return;
            if (Tabs.SelectedItem == HomeTab) await Stats.RefreshAsync();
            else if (Tabs.SelectedItem == ProgramsTab) await Programs.RefreshAsync();
            else if (Tabs.SelectedItem == PlayTab) await PlayTime.RefreshAsync();
            else if (Tabs.SelectedItem == HistoryTab) { await Summary.RefreshAsync(); await LoadHistoryAsync(); }
        };
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true; // closing the window keeps NetGuard in the tray
            Hide();
        };
    }

    public void AllowClose() { _allowClose = true; Close(); }

    public void OpenWizard()
    {
        if (!IsVisible) Show();
        new WizardWindow(this, _client, _monitor).ShowDialog();
        _ = _monitor.RefreshAsync();
    }

    // ================================================================ lifecycle / status

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        OnStatusChanged();
        _timer.Start();
        await Stats.RefreshAsync();
        _statsLoaded = DateTime.UtcNow;
        if (_status != null && (!_status.ConfigExists || !_status.RecoveryCodeExists))
            ShowMessage("Welcome to NetGuard. First time here? Settings → Run setup wizard.", ok: true, sticky: true);
    }

    private void OnStatusChanged()
    {
        _status = _monitor.Status;
        _serviceDown = _monitor.ServiceDown;
        _serviceProblem = _monitor.Problem;
        ApplyStatus();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_status?.TestDeadlineUtc is { } deadline)
            CountdownText.Text = TimeLeft(deadline - DateTimeOffset.UtcNow);
        if (_status?.Profiles.FirstOrDefault(x => x.IsActive)?.Play is { EndsUtc: { } pe })
            PlayLeftText.Text = TimeLeft(pe - ServerNow);
        if (_status?.WindowUntilUtc is { } wu)
            WindowLeftText.Text = TimeLeft(wu - ServerNow);
        UpdateUnlockPanel();
        if (IsVisible && Tabs.SelectedItem == HomeTab && DateTime.UtcNow - _statsLoaded > TimeSpan.FromMinutes(1))
        {
            _statsLoaded = DateTime.UtcNow;
            await Stats.RefreshAsync();
        }
    }

    private Task RefreshStatusAsync() => _monitor.RefreshAsync();

    /// <summary>NetGuard's trusted time (the PC clock may have been changed).</summary>
    private DateTimeOffset ServerNow => DateTimeOffset.UtcNow + _monitor.ServerOffset;

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
            CountdownPanel.Visibility = PlayPanel.Visibility = UnlockPanel.Visibility = PracticeStripe.Visibility = Visibility.Collapsed;
            ServiceText.Text = $"Service: {SafeServiceState()}.";
            StartServiceButton.Visibility = Visibility.Visible;
            BuildProfileCards(null);
            return;
        }
        var s = _status;

        // ---- settings
        ServiceText.Text = $"Running (version {s.ServiceVersion}), self-protection {(s.SelfProtectionActive ? "on" : "on standby until a lock starts")}. " +
                           $"Last check {s.LastCheckUtc?.ToLocalTime():T}." +
                           (s.LastTamperUtc is { } t ? $" Last repair of a change: {t.ToLocalTime():g}." : "");
        StartServiceButton.Visibility = Visibility.Collapsed;
        RecoveryStateText.Text = s.RecoveryCodeExists
            ? $"✓ Recovery code created {s.RecoveryCodeCreatedUtc?.ToLocalTime():d}."
            : "No recovery code yet – locks can't start until you create one.";
        RecoveryButton.Content = s.RecoveryCodeExists ? "Replace recovery code…" : "Create recovery code";
        RecoveryButton.IsEnabled = s.ActiveProfile == null;
        PracticeStartButton.IsEnabled = s.PracticeUntilUtc == null && s.ActiveProfile == null && s.RecoveryCodeExists;
        PracticeEndButton.IsEnabled = s.PracticeUntilUtc != null;
        var testable = s.Profiles.Where(p => p.TestAllowed).Select(p => p.Name).ToList();
        if (!TestProfileBox.Items.Cast<string>().SequenceEqual(testable))
        {
            var keep = TestProfileBox.SelectedItem as string;
            TestProfileBox.ItemsSource = testable;
            TestProfileBox.SelectedItem = testable.Contains(keep ?? "") ? keep : testable.FirstOrDefault();
        }
        TestLockButton.IsEnabled = testable.Count > 0;
        TestLockButton.ToolTip = testable.Count > 0 ? null : "Only possible with normal internet.";

        // ---- banner
        var profile = s.ActiveProfile;
        var active = s.Profiles.FirstOrDefault(x => x.IsActive);
        if (profile == null)
        {
            StatusTitle.Text = "Normal internet";
            StatusDetail.Text = !s.ConfigExists || !s.RecoveryCodeExists ? "NetGuard isn't fully set up yet – see Settings." : "No profile is active.";
            Banner.Background = Brush("#2E3A46");
        }
        else
        {
            StatusTitle.Text = $"{profile} mode" + (s.CommitmentUntilUtc is { } cu ? $" – committed until {Fmt(cu)}" : "");
            StatusDetail.Text = s.TestDeadlineUtc is { } d
                ? $"Test lock – normal internet comes back by itself at {d.ToLocalTime():T}."
                : s.MaintenanceUntilUtc is { } mu
                    ? $"Maintenance until {Fmt(mu)}, then back to your previous profile."
                    : active?.Play is { Allowed: true, EndsUtc: { } pe }
                        ? $"Play time until {Fmt(pe)}. Then NetGuard switches to Offline."
                        : $"Since {Fmt(s.ActiveSinceUtc ?? DateTimeOffset.UtcNow)}. NetGuard keeps it in place.";
            Banner.Background = Brush(Theme.ProfileColor(profile));
        }
        CountdownPanel.Visibility = s.TestDeadlineUtc != null ? Visibility.Visible : Visibility.Collapsed;
        WindowPanel.Visibility = s.WindowUntilUtc != null ? Visibility.Visible : Visibility.Collapsed;
        WindowLabel.Text = $"{s.WindowName} – time left";
        if (s.WindowUntilUtc is { } wu && profile != null)
            StatusDetail.Text = $"{s.WindowName} until {Fmt(wu)}: Chrome only reaches your list of websites, then everything closes again.";
        WindowButton.Content = $"🎸 {s.WindowName} ({s.WindowMinutes} min)";
        WindowButton.IsEnabled = s.WindowAllowed;
        WindowButton.ToolTip = s.WindowHint;
        WindowHintText.Text = s.WindowUntilUtc != null ? "" : s.WindowHint.EndsWith("left today") ? s.WindowHint : "";
        WindowButton.Visibility = s.ActiveProfile != null && s.Profiles.FirstOrDefault(x => x.IsActive)?.Kind != "AllowAll"
            ? Visibility.Visible : Visibility.Collapsed;
        var play = active?.Play;
        PlayPanel.Visibility = s.TestDeadlineUtc == null && s.WindowUntilUtc == null && play is { Allowed: true, EndsUtc: not null } ? Visibility.Visible : Visibility.Collapsed;
        GraceButton.Visibility = play?.GraceAvailable == true ? Visibility.Visible : Visibility.Collapsed;
        GraceButton.Content = $"+{s.GraceMinutes} min (once today)";
        PlayLabel.Text = play?.GraceActive == true ? "Extra minutes left" : "Play time left";

        PracticeStripe.Visibility = s.PracticeUntilUtc != null ? Visibility.Visible : Visibility.Collapsed;
        PracticeStripeText.Text = s.PracticeUntilUtc is { } pu
            ? $"🧪 PRACTICE MODE until {Fmt(pu)} – waiting times are short and nothing counts. Afterwards normal internet comes back by itself."
            : "";

        // ---- home
        CommitText.Text = s.CommitmentUntilUtc is { } cut ? $"Committed to {s.CommitmentProfile} until {Fmt(cut)}" : "";
        CommitButton.IsEnabled = s.RecoveryCodeExists && s.ConfigExists;

        var notices = new List<string>();
        notices.AddRange(s.RejectedFiles.Select(r => "⚠ Blocked because it changed or isn't genuine: " + r +
                                                     ". If it's a normal update: Programs → Re-approve (with normal internet or in Maintenance)."));
        if (s.MaintenanceReminder)
            notices.Add("🛠 No Maintenance for a while – Windows and Defender haven't been able to update. Consider a Maintenance unlock soon.");
        NoticeText.Text = string.Join("\n", notices);
        NoticeText.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateUnlockPanel();
        BuildProfileCards(s);
    }

    private static string SafeServiceState()
    {
        try { return ServiceControl.Describe(); } catch { return "unknown"; }
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private static string TimeLeft(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    private static string Fmt(DateTimeOffset utc)
    {
        var l = utc.ToLocalTime();
        return l.Date == DateTime.Today ? l.ToString("HH:mm") : l.Date == DateTime.Today.AddDays(1) ? "tomorrow " + l.ToString("HH:mm") : l.ToString("ddd dd.MM. HH:mm");
    }

    private static string Mins(int m) => m >= 60 ? $"{m / 60}h{(m % 60 > 0 ? $" {m % 60}min" : "")}" : $"{m}min";

    /// <summary>
    /// Active: when play time really ends. Not active: the time only counts once you switch, so show
    /// how long the window is open and how much play time is left instead of a moving end time.
    /// </summary>
    private static string PlayLine(PlayDto p, bool active)
    {
        string first;
        if (!p.Allowed) first = "⏸ " + p.Reason;
        else if (active) first = $"🎮 Play time until {(p.EndsUtc is { } e ? Fmt(e) : "–")}";
        else first = $"🎮 Play time open{(p.WindowEndsUtc is { } windowEnd ? $" until {Fmt(windowEnd)}" : "")}" +
                     (p.MinutesLeftToday is { } left ? $" · {Mins(left)} left" : "");
        var parts = new List<string> { first };
        if (p.DailyLimitMinutes is { } d) parts.Add($"today {Mins(p.UsedTodayMinutes)} / {Mins(d)}");
        if (p.WeeklyLimitMinutes is { } w) parts.Add($"week {Mins(p.UsedWeekMinutes)} / {Mins(w)}");
        return string.Join(" · ", parts);
    }

    // ================================================================ messages

    private void ShowMessage(string text, bool ok, bool sticky = false)
    {
        MessageText.Text = text;
        MessageBar.Background = Brush(ok ? "#E8F3F0" : "#FBE9E7");
        MessageText.Foreground = Brush(ok ? "#24584C" : "#8C2B1E");
        MessageBar.Visibility = Visibility.Visible;
        _messageTimer.Stop();
        if (ok && !sticky) _messageTimer.Start();
    }

    private void OnCloseMessage(object sender, RoutedEventArgs e) => MessageBar.Visibility = Visibility.Collapsed;

    // ================================================================ profile cards

    private void BuildProfileCards(StatusDto? status)
    {
        var sig = status == null ? "down" :
            string.Join("|", status.Profiles.Select(p => $"{p.Name}{p.IsActive}{p.SwitchAllowed}{p.SwitchHint}{p.UnlockAllowed}{p.UnlockHint}" +
                                                    (p.Play == null ? "" : PlayLine(p.Play, p.IsActive)))) + status.ConfigExists + status.UnlocksLeftToday;
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
            stack.Children.Add(new TextBlock { Text = p.Description, Style = (Style)FindResource("Body"), MinHeight = 44 });

            var name = p.Name;
            var needsUnlock = !p.IsActive && !p.SwitchAllowed && p.UnlockAllowed;
            var btn = new Button
            {
                Content = p.IsActive ? "Active" : needsUnlock ? "Request unlock…" : $"Switch to {p.Name}",
                Style = (Style)FindResource(needsUnlock ? "Secondary" : "Primary"),
                IsEnabled = !p.IsActive && (p.SwitchAllowed || p.UnlockAllowed),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            btn.Click += async (_, _) => { if (needsUnlock) await RequestUnlockAsync(name); else await SwitchAsync(name); };
            stack.Children.Add(btn);

            if (p.Play is { } play)
                stack.Children.Add(new TextBlock
                {
                    Text = PlayLine(play, p.IsActive), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                    Foreground = Brush(play.Allowed ? "#2E7D6B" : "#5F6B7A"),
                });
            if (!p.IsActive && !p.SwitchAllowed)
                stack.Children.Add(new TextBlock
                {
                    Text = p.UnlockAllowed ? $"Looser than now – needs an unlock request ({status.UnlocksLeftToday} left today)." : (p.UnlockHint.Length > 0 ? p.UnlockHint : p.SwitchHint),
                    FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#5F6B7A"), Margin = new Thickness(0, 4, 0, 0),
                });

            ProfileCards.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = stack });
        }
    }

    // ================================================================ running requests

    private enum ResultView { Message, Dialog }

    /// <summary>Send a request to the service; show the answer in the message bar (or a window for long texts).</summary>
    private async Task<IpcResponse?> RunAsync(string title, IpcRequest request, ResultView show = ResultView.Message)
    {
        if (_busy) return null;
        _busy = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var r = await _client.SendAsync(request);
            if (!r.Ok) ShowMessage(r.Error ?? "That didn't work.", ok: false);
            else if (show == ResultView.Dialog) Dialogs.ShowText(this, title, r.Text ?? "");
            else ShowMessage(FirstParagraph(r.Text) ?? $"{title}: done.", ok: true);
            return r;
        }
        catch (Exception e)
        {
            ShowMessage($"{title} – didn't work: {e.Message}", ok: false);
            return null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _busy = false;
            _cardsSignature = "";
            await RefreshStatusAsync();
            _ = Stats.RefreshAsync();
        }
    }

    private static string? FirstParagraph(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Replace("\r", "").Split("\n\n")[0].Trim();

    private async Task SwitchAsync(string profile)
    {
        var p = _status?.Profiles.FirstOrDefault(x => x.Name == profile);
        if (p == null) return;
        var msg = $"Switch to {p.Name} now?\n\n{p.Description}\n\n" +
                  (p.Play != null
                      ? "Play time counts while this profile is active. When it's over, NetGuard switches to Offline by itself."
                      : "Stricter profiles are always one click away; looser ones need an unlock request.");
        if (MessageBox.Show(this, msg, "Switch profile", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunAsync($"Switch to {p.Name}", new IpcRequest { Command = IpcCommands.Switch, Profile = profile });
    }

    private async void OnStartTestLock(object sender, RoutedEventArgs e)
    {
        if (TestProfileBox.SelectedItem is not string profile) return;
        var minutes = int.Parse((string)((ComboBoxItem)MinutesBox.SelectedItem).Tag);
        if (MessageBox.Show(this, $"Start a {minutes}-minute {profile} test lock?\n\nIt ends by itself (or with \"End test lock now\" in the banner).",
                "Test lock", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunAsync($"Test lock: {profile}", new IpcRequest { Command = IpcCommands.TestStart, Profile = profile, Minutes = minutes });
        Tabs.SelectedItem = HomeTab;
    }

    private async void OnRestoreNow(object sender, RoutedEventArgs e) =>
        await RunAsync("End test lock", new IpcRequest { Command = IpcCommands.TestEnd });

    // ================================================================ unlock requests

    private string? _shownPassage;

    private void UpdateUnlockPanel()
    {
        var u = _status?.Unlock;
        if (u == null)
        {
            UnlockPanel.Visibility = Visibility.Collapsed;
            _shownPassage = null;
            return;
        }
        UnlockPanel.Visibility = Visibility.Visible;
        var now = ServerNow;
        if (now < u.ReadyUtc)
        {
            UnlockTitle.Text = $"Unlock to {u.TargetProfile} – waiting {TimeLeft(u.ReadyUtc - now)}";
            UnlockInfo.Text = $"Your reason: \"{u.Reason}\"\n\nThe waiting time can't be shortened. Then you'll have " +
                              $"{(u.ExpiresUtc - u.ReadyUtc).TotalMinutes:0} minutes to confirm by retyping a passage – or just let it expire.";
            ConfirmArea.Visibility = Visibility.Collapsed;
            ConfirmUnlockButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            UnlockTitle.Text = $"Unlock to {u.TargetProfile} – {TimeLeft(u.ExpiresUtc - now)} left to confirm";
            UnlockInfo.Text = $"Your reason: \"{u.Reason}\"\nStill want this? If not, just let it expire.";
            ConfirmArea.Visibility = Visibility.Visible;
            ConfirmUnlockButton.Visibility = Visibility.Visible;
            if (u.Passage != null && _shownPassage != u.Passage)
            {
                _shownPassage = u.Passage;
                PassageText.Text = u.Passage;
                PassageInput.Text = "";
            }
        }
    }

    private void OnPassageChanged(object sender, TextChangedEventArgs e)
    {
        var expected = _shownPassage ?? "";
        var typed = PassageInput.Text;
        var firstWrong = Enumerable.Range(0, Math.Min(typed.Length, expected.Length)).FirstOrDefault(i => typed[i] != expected[i], -1);
        if (typed.Length > expected.Length) firstWrong = expected.Length;
        PassageProgress.Text = firstWrong >= 0
            ? $"✗ Typo at character {firstWrong + 1} – fix it to continue."
            : $"✓ Correct so far – {typed.Length} of {expected.Length} characters.";
        PassageProgress.Foreground = Brush(firstWrong >= 0 ? "#B03A2E" : "#2E7D6B");
    }

    private async Task RequestUnlockAsync(string profile)
    {
        if (_status == null) return;
        var reason = Dialogs.AskUnlockReason(this, profile, _status.UnlocksLeftToday);
        if (reason == null) return;
        await RunAsync($"Unlock request: {profile}", new IpcRequest { Command = IpcCommands.UnlockRequest, Profile = profile, Text = reason });
    }

    private async void OnConfirmUnlock(object sender, RoutedEventArgs e) =>
        await RunAsync("Confirm unlock", new IpcRequest { Command = IpcCommands.UnlockConfirm, Text = PassageInput.Text });

    private async void OnCancelUnlock(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Cancel the unlock request? It still counts as one of today's requests.", "Unlock",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunAsync("Cancel unlock", new IpcRequest { Command = IpcCommands.UnlockCancel });
    }

    private async void OnGrace(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Take {_status?.GraceMinutes} more minutes? This is possible once a day, and they come off tomorrow's play time.",
                "Extra minutes", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunAsync("Extra minutes", new IpcRequest { Command = IpcCommands.Grace });
    }

    private async void OnCommit(object sender, RoutedEventArgs e)
    {
        if (_status == null) return;
        var choice = Dialogs.AskCommitment(this, _status);
        if (choice == null) return;
        var (profile, until) = choice.Value;
        if (MessageBox.Show(this, $"Commit to {profile} until {until:dddd HH:mm}?\n\nUntil then nothing looser is possible – no unlock requests. " +
                                  "You can extend a commitment, but never shorten it.",
                "Commit", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunAsync("Commit", new IpcRequest { Command = IpcCommands.Commit, Profile = profile, Until = new DateTimeOffset(until) });
    }

    private async void OnWindowStart(object sender, RoutedEventArgs e)
    {
        if (_status == null) return;
        if (MessageBox.Show(this, $"Open {_status.WindowName} for {_status.WindowMinutes} minutes?\n\n" +
                                  "Chrome gets internet, but only for your list of websites (Programs → " + _status.WindowName + "), " +
                                  $"plus the other programs on that list. Afterwards everything closes again by itself.\n\n{_status.WindowHint}.",
                _status.WindowName, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunAsync(_status.WindowName, new IpcRequest { Command = IpcCommands.WindowStart });
    }

    private async void OnWindowEnd(object sender, RoutedEventArgs e) =>
        await RunAsync("Close", new IpcRequest { Command = IpcCommands.WindowEnd });

    // ================================================================ settings

    private void OnRunWizard(object sender, RoutedEventArgs e) => OpenWizard();

    private async void OnPracticeStart(object sender, RoutedEventArgs e)
    {
        if (await RunAsync("Practice mode", new IpcRequest { Command = IpcCommands.PracticeStart, Minutes = 30 }) is { Ok: true })
            Tabs.SelectedItem = HomeTab;
    }

    private async void OnPracticeEnd(object sender, RoutedEventArgs e) =>
        await RunAsync("End practice mode", new IpcRequest { Command = IpcCommands.PracticeEnd });

    private async void OnCreateRecoveryCode(object sender, RoutedEventArgs e)
    {
        if (_status?.RecoveryCodeExists == true &&
            MessageBox.Show(this, "Create a new recovery code? The old one stops working.", "Recovery code",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        try
        {
            var r = await _client.CallAsync(new IpcRequest { Command = IpcCommands.RecoveryCreate });
            Dialogs.ShowNewRecoveryCode(this, r.Text!);
            ShowMessage("Recovery code created. Keep the paper safe.", ok: true);
        }
        catch (Exception ex) { ShowMessage("Couldn't create a recovery code: " + ex.Message, ok: false); }
        await RefreshStatusAsync();
    }

    private async void OnRecover(object sender, RoutedEventArgs e)
    {
        string? code = null;
        if (_status?.ActiveProfile != null)
        {
            code = Dialogs.AskRecoveryCode(this, $"Emergency restore ends the {_status.ActiveProfile} lock now and brings back normal internet. It will be logged.");
            if (code == null) return;
        }
        else if (MessageBox.Show(this, "No lock is active. Run the restore anyway (removes any NetGuard rules left over)?",
                     "Emergency restore", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await RunAsync("Emergency restore", new IpcRequest { Command = IpcCommands.Recover, Code = code });
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
        var uninstaller = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Environment.ProcessPath!)!, "unins000.exe");
        try
        {
            Process.Start(new ProcessStartInfo(uninstaller) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn't start the uninstaller (" + ex.Message + "). Use Windows Settings → Apps → NetGuard → Uninstall.", ok: false);
        }
    }

    private async void OnStatus(object sender, RoutedEventArgs e) =>
        await RunAsync("Status", new IpcRequest { Command = IpcCommands.StatusText }, ResultView.Dialog);

    private async void OnAuditOn(object sender, RoutedEventArgs e) =>
        await RunAsync("Start recording", new IpcRequest { Command = IpcCommands.Audit, On = true });

    private async void OnAuditOff(object sender, RoutedEventArgs e) =>
        await RunAsync("Stop recording", new IpcRequest { Command = IpcCommands.Audit, On = false });

    private async void OnShowBlocked(object sender, RoutedEventArgs e) =>
        await RunAsync("Blocked programs (last 10 minutes)", new IpcRequest { Command = IpcCommands.Blocked }, ResultView.Dialog);

    private void OnStartService(object sender, RoutedEventArgs e)
    {
        var ngctl = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Environment.ProcessPath!)!, "ngctl.exe");
        try
        {
            var p = Process.Start(new ProcessStartInfo(ngctl, "service on") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(30_000);
            ShowMessage("Asked Windows to start the NetGuard protection service.", ok: true);
        }
        catch (Win32Exception) { }
        catch (Exception ex) { ShowMessage("Couldn't start the service: " + ex.Message, ok: false); }
        _ = RefreshStatusAsync();
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        InstallUpdateButton.IsEnabled = false;
        try
        {
            _latest = await Updater.CheckLatestAsync();
            var current = GitHubReleases.CurrentVersion;
            if (_latest.Version <= current) ShowMessage($"You have the newest version ({current}).", ok: true);
            else
            {
                ShowMessage($"Version {_latest.Version} is available (you have {current}).", ok: true, sticky: true);
                InstallUpdateButton.IsEnabled = true;
                VersionText.Text = $"Installed version: {current} – version {_latest.Version} is available.";
            }
        }
        catch (Exception ex) { ShowMessage("Couldn't check for updates: " + ex.Message, ok: false); }
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

    // ================================================================ history

    private async void OnRefreshHistory(object sender, RoutedEventArgs e)
    {
        await Summary.RefreshAsync();
        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var r = await _client.SendAsync(new IpcRequest { Command = IpcCommands.History }, TimeSpan.FromSeconds(15));
            var rows = (r.History ?? new()).Select(h => new LogRow(h.Ts.ToLocalTime().ToString("ddd dd.MM. HH:mm:ss"), h.What, h.Details)).ToList();
            HistoryList.ItemsSource = rows;
            HistoryCount.Text = rows.Count == 0 ? "Nothing logged yet." : $"Everything NetGuard did – {rows.Count} entries, newest first";
        }
        catch (GuardClient.ServiceUnavailableException) { HistoryCount.Text = "The service isn't running, so the history can't be shown."; }
        catch (Exception e) { HistoryCount.Text = "Couldn't load the history: " + e.Message; }
    }
}
