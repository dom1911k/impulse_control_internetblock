using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetGuard.Core.Config;
using NetGuard.Core.Ipc;
using NetGuard.Windows;

namespace NetGuard.App;

/// <summary>
/// The play-time plan editor: weekly timetable, daily/weekly limits, which programs count as "the game".
/// Stricter plans apply right away; looser ones take effect after 72 hours (the service decides).
/// </summary>
public sealed class PlayTimePanel : ScrollViewer
{
    private readonly Window _owner;
    private readonly GuardClient _client;
    private readonly StackPanel _root = new() { Margin = new Thickness(4, 14, 14, 14) };
    private List<PlanDto> _plans = new();
    private string? _profile;
    private PlayPlan? _draft;   // what the user is editing (null = no plan)
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x5F, 0x6B, 0x7A));
    private static readonly SolidColorBrush Ink = new(Color.FromRgb(0x1F, 0x29, 0x33));
    private static readonly SolidColorBrush Line = new(Color.FromRgb(0xDD, 0xE3, 0xEA));
    private static readonly DayOfWeek[] Week =
        { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };

    public PlayTimePanel(Window owner, GuardClient client)
    {
        _owner = owner;
        _client = client;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Content = _root;
    }

    private static Button Btn(string text, bool primary) =>
        new() { Content = text, Style = (Style)Application.Current.FindResource(primary ? "Primary" : "Secondary"), HorizontalAlignment = HorizontalAlignment.Left };

    private static TextBlock T(string s, double size = 14, bool bold = false, Brush? color = null) => new()
    {
        Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = color ?? Ink,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
    };

    public async Task RefreshAsync()
    {
        try
        {
            var r = await _client.SendAsync(new IpcRequest { Command = IpcCommands.PlanGet });
            if (!r.Ok) { Show(r.Error); return; }
            _plans = r.Plans ?? new();
        }
        catch (Exception e) { Show("Couldn't load the play plans: " + e.Message); return; }
        _profile ??= _plans.FirstOrDefault(p => p.Plan != null)?.Profile ?? _plans.FirstOrDefault(p => p.Profile.Equals("WoW", StringComparison.OrdinalIgnoreCase))?.Profile ?? _plans.FirstOrDefault()?.Profile;
        _draft = Clone(_plans.FirstOrDefault(p => p.Profile == _profile)?.Plan);
        Render();
    }

    private void Show(string? text)
    {
        _root.Children.Clear();
        _root.Children.Add(T(text ?? "", 14, false, Muted));
    }

    private static PlayPlan? Clone(PlayPlan? p) => p == null ? null : new PlayPlan
    {
        Windows = p.Windows.Select(w => new PlayWindow { Day = w.Day, Start = w.Start, End = w.End }).ToList(),
        DailyLimitMinutes = p.DailyLimitMinutes,
        WeeklyLimitMinutes = p.WeeklyLimitMinutes,
        GamePrograms = p.GamePrograms.ToList(),
        FallbackProfile = p.FallbackProfile,
    };

    private void Render()
    {
        _root.Children.Clear();
        if (_plans.Count == 0) { Show("Set up NetGuard first (Setup → Run setup wizard)."); return; }
        var current = _plans.First(p => p.Profile == _profile);

        _root.Children.Add(T("Play time", 20, true));
        _root.Children.Add(T("Decide when a game profile may be active, and for how long. Outside these times NetGuard keeps you in the fallback " +
                             "profile, and the game itself is blocked in every profile – even in Maintenance. Play time counts while the profile is active. " +
                             "Stricter changes always apply right away. Looser ones (more time, more windows, higher limits) apply right away only while " +
                             "no profile is active – during a lock they take 72 hours. So set up and fine-tune your plan before you start a lock.", 13, false, Muted));

        var pick = new ComboBox { MinWidth = 200, FontSize = 14, Margin = new Thickness(0, 6, 0, 12), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var p in _plans) pick.Items.Add(p.Profile);
        pick.SelectedItem = _profile;
        pick.SelectionChanged += (_, _) => { _profile = pick.SelectedItem as string; _draft = Clone(_plans.First(p => p.Profile == _profile).Plan); Render(); };
        _root.Children.Add(pick);

        if (current.HasPending)
        {
            var pending = new Border { Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xE5)), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 12) };
            var sp = new StackPanel();
            sp.Children.Add(T($"A looser change is waiting and takes effect {current.PendingEffectiveUtc?.ToLocalTime():dddd dd.MM. HH:mm}: " +
                              Describe(current.PendingPlan), 13));
            var cancel = Btn("Cancel the waiting change", false);
            cancel.Click += async (_, _) => await Send(new IpcRequest { Command = IpcCommands.PlanCancel, Profile = _profile });
            sp.Children.Add(cancel);
            pending.Child = sp;
            _root.Children.Add(pending);
        }

        var use = new CheckBox { Content = $"{_profile} has a play plan", IsChecked = _draft != null, FontSize = 14, Margin = new Thickness(0, 0, 0, 12) };
        use.Click += (_, _) =>
        {
            _draft = use.IsChecked == true
                ? new PlayPlan
                {
                    Windows = Week.Select(d => new PlayWindow { Day = d, Start = "19:00", End = "22:00" }).ToList(),
                    DailyLimitMinutes = 120,
                    GamePrograms = current.Programs.Where(n => n.Contains("Warcraft", StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(current.Programs.FirstOrDefault() ?? "").Where(n => n.Length > 0).ToList(),
                    FallbackProfile = current.FallbackChoices.FirstOrDefault(f => f.Equals("Offline", StringComparison.OrdinalIgnoreCase)) ?? current.FallbackChoices.FirstOrDefault() ?? "Offline",
                }
                : null;
            Render();
        };
        _root.Children.Add(use);

        if (_draft != null)
        {
            _root.Children.Add(Card(Timetable()));
            _root.Children.Add(Card(Limits()));
            _root.Children.Add(Card(Games(current)));
        }

        var save = Btn("Save play plan", true);
        save.Margin = new Thickness(0, 12, 0, 0);
        save.Click += async (_, _) => await SaveAsync();
        _root.Children.Add(save);
        _root.Children.Add(_status);
    }

    private static Border Card(UIElement child) => new()
    {
        Background = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12), Child = child,
    };

    private static List<string> Times(bool end)
    {
        var list = new List<string>();
        for (var m = end ? 30 : 0; m <= (end ? 1440 : 1410); m += 30) list.Add($"{m / 60:00}:{m % 60:00}");
        return list;
    }

    private UIElement Timetable()
    {
        var sp = new StackPanel();
        sp.Children.Add(T("When", 16, true));
        foreach (var day in Week)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new TextBlock { Text = day.ToString(), Width = 100, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });

            var add = new StackPanel { Orientation = Orientation.Horizontal };
            var from = new ComboBox { ItemsSource = Times(false), SelectedItem = "19:00", Width = 75 };
            var to = new ComboBox { ItemsSource = Times(true), SelectedItem = "22:00", Width = 75, Margin = new Thickness(4, 0, 4, 0) };
            var addBtn = new Button { Content = "+ add", Style = (Style)Application.Current.FindResource("Secondary"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0) };
            var d = day;
            addBtn.Click += (_, _) =>
            {
                var w = new PlayWindow { Day = d, Start = (string)from.SelectedItem, End = (string)to.SelectedItem == "24:00" ? "24:00" : (string)to.SelectedItem };
                if (w.EndMinute <= w.StartMinute) { _status.Text = "The end has to be after the start."; return; }
                _draft!.Windows.Add(w);
                Render();
            };
            add.Children.Add(from);
            add.Children.Add(new TextBlock { Text = "–", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
            add.Children.Add(to);
            add.Children.Add(addBtn);
            DockPanel.SetDock(add, Dock.Right);
            row.Children.Add(add);

            var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            var windows = _draft!.Windows.Where(w => w.Day == day).OrderBy(w => w.StartMinute).ToList();
            if (windows.Count == 0) chips.Children.Add(new TextBlock { Text = "no play time", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            foreach (var w in windows)
            {
                var chip = new Button
                {
                    Content = $"{w.Start}–{w.End}  ✕", ToolTip = "Remove", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 2, 8, 2),
                    Style = (Style)Application.Current.FindResource("Secondary"),
                };
                var ww = w;
                chip.Click += (_, _) => { _draft.Windows.Remove(ww); Render(); };
                chips.Children.Add(chip);
            }
            row.Children.Add(chips);
            sp.Children.Add(row);
        }
        return sp;
    }

    private UIElement Limits()
    {
        var sp = new StackPanel();
        sp.Children.Add(T("How long", 16, true));

        ComboBox Limit(int? value, IEnumerable<int> options, Func<int, string> label)
        {
            var cb = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
            cb.Items.Add(new ComboBoxItem { Content = "no limit", Tag = null });
            foreach (var o in options) cb.Items.Add(new ComboBoxItem { Content = label(o), Tag = o });
            cb.SelectedItem = cb.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value)) ?? cb.Items[0];
            return cb;
        }
        static string H(int m) => m % 60 == 0 ? $"{m / 60} h" : $"{m / 60} h {m % 60} min";

        sp.Children.Add(T("Per day", 13));
        var daily = Limit(_draft!.DailyLimitMinutes, Enumerable.Range(1, 16).Select(i => i * 30), H);
        daily.SelectionChanged += (_, _) => _draft.DailyLimitMinutes = (int?)((ComboBoxItem)daily.SelectedItem).Tag;
        sp.Children.Add(daily);
        sp.Children.Add(T("Per week", 13));
        var weekly = Limit(_draft.WeeklyLimitMinutes, Enumerable.Range(1, 40).Select(i => i * 60), H);
        weekly.SelectionChanged += (_, _) => _draft.WeeklyLimitMinutes = (int?)((ComboBoxItem)weekly.SelectedItem).Tag;
        sp.Children.Add(weekly);
        sp.Children.Add(T("When the time is up you get warnings at 15, 5 and 1 minute; then NetGuard switches. Once a day you can take 10 extra minutes – " +
                          "they come off the next day.", 12, false, Muted));
        return sp;
    }

    private UIElement Games(PlanDto current)
    {
        var sp = new StackPanel();
        sp.Children.Add(T("What counts as the game", 16, true));
        sp.Children.Add(T("These programs are blocked everywhere outside play time. Leave launchers and music unticked.", 12, false, Muted));
        foreach (var prog in current.Programs)
        {
            var cb = new CheckBox { Content = prog, IsChecked = _draft!.GamePrograms.Contains(prog, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 2, 0, 2) };
            var name = prog;
            cb.Click += (_, _) =>
            {
                _draft.GamePrograms.RemoveAll(g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (cb.IsChecked == true) _draft.GamePrograms.Add(name);
            };
            sp.Children.Add(cb);
        }
        sp.Children.Add(T("Outside play time, switch to", 13));
        var fb = new ComboBox { ItemsSource = current.FallbackChoices, SelectedItem = _draft!.FallbackProfile, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        fb.SelectionChanged += (_, _) => _draft.FallbackProfile = (string)fb.SelectedItem;
        sp.Children.Add(fb);
        return sp;
    }

    private static string Describe(PlayPlan? p)
    {
        if (p == null) return "no play plan (unlimited).";
        var days = string.Join(", ", p.Windows.GroupBy(w => w.Day).Select(g => $"{g.Key.ToString()[..3]} " + string.Join("+", g.Select(w => $"{w.Start}–{w.End}"))));
        return $"{days}; per day {(p.DailyLimitMinutes is { } d ? $"{d} min" : "unlimited")}, per week {(p.WeeklyLimitMinutes is { } w2 ? $"{w2 / 60} h" : "unlimited")}.";
    }

    private async Task SaveAsync()
    {
        if (MessageBox.Show(_owner, $"Save this play plan for {_profile}?\n\n{Describe(_draft)}\n\nWhile no profile is active it applies right away. During a lock, a looser plan only takes effect in 72 hours.",
                "Play plan", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await Send(new IpcRequest { Command = IpcCommands.PlanSet, Profile = _profile, Plan = _draft });
    }

    private async Task Send(IpcRequest req)
    {
        string msg;
        try { msg = (await _client.CallAsync(req)).Text ?? ""; }
        catch (Exception e) { msg = e.Message; }
        await RefreshAsync();
        _status.Text = msg;
    }
}
