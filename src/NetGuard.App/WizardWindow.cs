using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetGuard.Core.Ipc;
using NetGuard.Windows;

namespace NetGuard.App;

/// <summary>
/// First-run setup: welcome → find apps → check programs → recovery code → recovery test → starting profile.
/// </summary>
public sealed class WizardWindow : Window
{
    private readonly GuardClient _client;
    private readonly StatusMonitor _monitor;
    private readonly ContentControl _page = new();
    private readonly TextBlock _stepTitle = new() { FontSize = 13, Foreground = Muted, Margin = new Thickness(0, 0, 0, 4) };
    private readonly Button _back, _next, _skip;
    private int _step;
    private string? _startProfile;

    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x5F, 0x6B, 0x7A));
    private static readonly SolidColorBrush Ink = new(Color.FromRgb(0x1F, 0x29, 0x33));
    private static readonly string[] Titles = { "Welcome", "Find your apps", "Check the programs", "Recovery code", "Recovery test", "Start" };

    public WizardWindow(Window owner, GuardClient client, StatusMonitor monitor)
    {
        _client = client;
        _monitor = monitor;
        Owner = owner;
        Title = "NetGuard setup";
        Width = 780;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        ResizeMode = ResizeMode.CanMinimize;

        _back = Btn("Back", false);
        _next = Btn("Next", true);
        _skip = Btn("Skip this step", false);
        _back.Click += (_, _) => { if (_step > 0) Go(_step - 1); };
        _next.Click += async (_, _) => await NextAsync();
        _skip.Click += (_, _) => Go(_step + 1);

        var buttons = new DockPanel { Margin = new Thickness(28, 12, 28, 20) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_skip);
        right.Children.Add(_back);
        right.Children.Add(_next);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var header = new StackPanel { Margin = new Thickness(28, 22, 28, 8) };
        header.Children.Add(_stepTitle);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _page, Padding = new Thickness(28, 0, 28, 0) });
        Content = root;
        Loaded += (_, _) => Go(0);
    }

    private static Button Btn(string text, bool primary) =>
        new() { Content = text, Style = (Style)Application.Current.FindResource(primary ? "Primary" : "Secondary"), MinWidth = 90 };

    private static TextBlock H(string s) => new() { Text = s, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Ink, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
    private static TextBlock P(string s) => new() { Text = s, TextWrapping = TextWrapping.Wrap, Foreground = Ink, Margin = new Thickness(0, 0, 0, 12), LineHeight = 21 };

    private static TextBox Mono(string text) => new()
    {
        Text = text, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        Background = Brushes.White, Padding = new Thickness(10), MinHeight = 140, MaxHeight = 280,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 12),
    };

    private void Go(int step)
    {
        if (step >= Titles.Length) { DialogResult = true; return; }
        _step = step;
        _stepTitle.Text = $"Step {step + 1} of {Titles.Length} · {Titles[step]}";
        _back.IsEnabled = step > 0;
        _next.Content = step == Titles.Length - 1 ? "Finish" : "Next";
        _skip.Visibility = step is 4 ? Visibility.Visible : Visibility.Collapsed;
        _next.IsEnabled = true;
        var page = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        _page.Content = page;
        switch (step)
        {
            case 0: Welcome(page); break;
            case 1: FindApps(page); break;
            case 2: _ = CheckPrograms(page); break;
            case 3: RecoveryCode(page); break;
            case 4: RecoveryTest(page); break;
            case 5: Start(page); break;
        }
    }

    private async Task NextAsync()
    {
        if (_step == Titles.Length - 1)
        {
            if (_startProfile != null)
            {
                try { await _client.CallAsync(new IpcRequest { Command = IpcCommands.Switch, Profile = _startProfile }); }
                catch (Exception e) { MessageBox.Show(this, e.Message, "NetGuard", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            }
            DialogResult = true;
            return;
        }
        Go(_step + 1);
    }

    // ---------------------------------------------------------------- pages

    private void Welcome(StackPanel page)
    {
        page.Children.Add(H("Let's set up NetGuard"));
        page.Children.Add(P("NetGuard blocks the internet for everything except the programs a profile allows – for example World of Warcraft, " +
                            "Battle.net and Spotify in the WoW profile. Steam is always blocked."));
        page.Children.Add(P("Making things stricter is always one click. Making them looser is deliberately slow. While a lock is active, " +
                            "NetGuard protects itself – even from your administrator account – and undoes changes to the firewall within seconds."));
        page.Children.Add(P("Your way out in an emergency is a recovery code that you'll write on paper in step 4."));
        page.Children.Add(P("This takes about five minutes."));
    }

    private void FindApps(StackPanel page)
    {
        page.Children.Add(H("Find your apps"));
        var exists = _monitor.Status?.ConfigExists == true;
        page.Children.Add(P(exists
            ? "NetGuard is already configured. Keep the current configuration, or search again (this replaces the program lists with what's found now)."
            : "NetGuard looks for World of Warcraft, Battle.net, the Blizzard Agent, Spotify and Steam on this PC."));
        var output = Mono(exists ? "Current configuration kept." : "");
        var search = Btn(exists ? "Search again (replaces)" : "Find my apps", !exists);
        search.HorizontalAlignment = HorizontalAlignment.Left;
        search.Margin = new Thickness(0, 0, 0, 12);
        _next.IsEnabled = exists;
        search.Click += async (_, _) =>
        {
            search.IsEnabled = false;
            output.Text = "Searching …";
            try
            {
                var r = await _client.CallAsync(new IpcRequest { Command = IpcCommands.Init });
                output.Text = r.Text;
                if (r.Text?.Contains("Microsoft Store version") == true)
                    output.Text += "\n\n⚠ Please replace the Store version of Spotify with the desktop version from https://download.scdn.co/SpotifySetup.exe, then search again.";
                _next.IsEnabled = true;
                await _monitor.RefreshAsync();
            }
            catch (Exception e) { output.Text = "Didn't work: " + e.Message; }
            search.IsEnabled = true;
        };
        page.Children.Add(search);
        page.Children.Add(output);
    }

    private async Task CheckPrograms(StackPanel page)
    {
        page.Children.Add(H("Check the programs"));
        page.Children.Add(P("These exact files may use the internet in each profile. Every Blizzard and Spotify file should say \"signed by …\". " +
                            "Anything marked REJECTED stays offline. You can change the lists any time in the Programs tab."));
        var output = Mono("Loading …");
        output.MaxHeight = 330;
        page.Children.Add(output);
        try { output.Text = (await _client.CallAsync(new IpcRequest { Command = IpcCommands.Programs })).Text; }
        catch (Exception e) { output.Text = "Couldn't load: " + e.Message; }
    }

    private void RecoveryCode(StackPanel page)
    {
        page.Children.Add(H("Your recovery code"));
        page.Children.Add(P("The code is your emergency key: it ends a lock early and lets you uninstall NetGuard during a lock. " +
                            "It's shown exactly once. Have paper and a pen ready – keep it somewhere inconvenient, or give it to someone you trust."));
        var state = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        var create = Btn("Create my recovery code", true);
        create.HorizontalAlignment = HorizontalAlignment.Left;
        void Refresh()
        {
            var has = _monitor.Status?.RecoveryCodeExists == true;
            state.Text = has ? "✓ You have a recovery code." : "No recovery code yet – locks can't start without one.";
            create.Content = has ? "Create a new one (the old one stops working)" : "Create my recovery code";
            _next.IsEnabled = has;
        }
        create.Click += async (_, _) =>
        {
            try
            {
                var r = await _client.CallAsync(new IpcRequest { Command = IpcCommands.RecoveryCreate });
                Dialogs.ShowNewRecoveryCode(this, r.Text!);
                await _monitor.RefreshAsync();
            }
            catch (Exception e) { state.Text = "Didn't work: " + e.Message; return; }
            Refresh();
        };
        page.Children.Add(state);
        page.Children.Add(create);
        Refresh();
    }

    private void RecoveryTest(StackPanel page)
    {
        page.Children.Add(H("Recovery test"));
        page.Children.Add(P("A one-minute Offline lock checks that blocking works and – more importantly – that normal internet comes back by itself. " +
                            "Your internet will be off for about a minute."));
        var log = new StackPanel { Margin = new Thickness(0, 4, 0, 12) };
        var run = Btn("Run the 1-minute test", true);
        run.HorizontalAlignment = HorizontalAlignment.Left;
        var canTest = _monitor.Status?.ActiveProfile == null;
        if (!canTest) page.Children.Add(P("A lock is active right now, so the test can't run. Skip this step."));
        run.IsEnabled = canTest;

        void Line(string text, bool? ok = null) => log.Children.Add(new TextBlock
        {
            Text = (ok == null ? "…  " : ok.Value ? "✅ " : "❌ ") + text, Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap,
        });

        run.Click += async (_, _) =>
        {
            run.IsEnabled = _next.IsEnabled = _back.IsEnabled = _skip.IsEnabled = false;
            log.Children.Clear();
            try
            {
                var before = await Online();
                Line("Internet works before the test", before);

                await _client.CallAsync(new IpcRequest { Command = IpcCommands.TestStart, Profile = "Offline", Minutes = 1 });
                Line("Offline test lock started (1 minute)", true);
                await Task.Delay(TimeSpan.FromSeconds(8));
                var during = await Online();
                Line("Internet is blocked during the lock", !during);

                Line("Waiting for the lock to end by itself …");
                var deadline = DateTime.UtcNow.AddMinutes(3);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    await _monitor.RefreshAsync();
                    if (_monitor.Status?.ActiveProfile == null) break;
                }
                var ended = _monitor.Status?.ActiveProfile == null;
                Line("The lock ended by itself", ended);
                await Task.Delay(TimeSpan.FromSeconds(3));
                var after = await Online();
                Line("Internet works again", after);

                var ok = before && !during && ended && after;
                log.Children.Add(P(ok ? "All good – NetGuard blocks, and it lets go again by itself."
                    : "Something didn't go as expected. If your internet still doesn't work: NetGuard → Tools → Emergency restore."));
            }
            catch (Exception e) { Line("The test couldn't run: " + e.Message, false); }
            run.IsEnabled = _next.IsEnabled = _back.IsEnabled = _skip.IsEnabled = true;
        };
        page.Children.Add(run);
        page.Children.Add(log);
    }

    /// <summary>Can this program reach the internet? (NetGuard itself is never on an allowlist, so this is blocked during any lock.)</summary>
    private static async Task<bool> Online()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var text = await http.GetStringAsync("http://www.msftconnecttest.com/connecttest.txt");
            return text.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void Start(StackPanel page)
    {
        page.Children.Add(H("How do you want to start?"));
        page.Children.Add(P("You can switch any time from the main window or the tray icon. Remember: stricter is always one click; " +
                            "looser needs the unlock process (coming in milestone 4) – until then only your recovery code ends a lock."));
        var options = new StackPanel();
        void Add(string? profile, string label, string hint)
        {
            var rb = new RadioButton { GroupName = "start", Margin = new Thickness(0, 6, 0, 6), IsChecked = profile == null };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = hint, Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            rb.Content = sp;
            rb.Checked += (_, _) => _startProfile = profile;
            options.Children.Add(rb);
        }
        Add(null, "Keep normal internet for now", "Nothing is locked yet. Start a profile whenever you're ready.");
        foreach (var p in _monitor.Status?.Profiles ?? new())
            if (p.SwitchAllowed && p.Kind != "AllowAll")
                Add(p.Name, $"Start in {p.Name}", p.Description);
        _startProfile = null;
        page.Children.Add(options);
    }
}
