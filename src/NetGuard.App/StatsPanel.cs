using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NetGuard.Core.Ipc;
using NetGuard.Core.Reporting;
using NetGuard.Windows;

namespace NetGuard.App;

/// <summary>The dashboard on the Home tab: play time, a 30-day chart, streaks and other numbers.</summary>
public sealed class StatsPanel : StackPanel
{
    private readonly GuardClient _client;
    private bool _loading;

    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x5F, 0x6B, 0x7A));
    private static readonly SolidColorBrush Ink = new(Color.FromRgb(0x1F, 0x29, 0x33));
    private static readonly SolidColorBrush Line = new(Color.FromRgb(0xDD, 0xE3, 0xEA));
    private static readonly SolidColorBrush Soft = new(Color.FromRgb(0xF4, 0xF6, 0xF8));

    public StatsPanel(GuardClient client) => _client = client;

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private static string H(int m) => m >= 60 ? $"{m / 60}h {m % 60:00}m" : $"{m}m";

    private static TextBlock T(string s, double size = 13, bool bold = false, Brush? color = null, Thickness? margin = null) => new()
    {
        Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = color ?? Ink,
        TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0),
    };

    private static Border Card(UIElement child) => new()
    {
        Background = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
        Padding = new Thickness(18), Margin = new Thickness(0, 0, 14, 14), Child = child,
    };

    private static Border Tile(string label, string value, string? sub, string? color = null)
    {
        var sp = new StackPanel();
        sp.Children.Add(T(label, 12, false, Muted));
        sp.Children.Add(T(value, 26, true, color == null ? Ink : Brush(color), new Thickness(0, 2, 0, 0)));
        if (sub != null) sp.Children.Add(T(sub, 12, false, Muted));
        return new Border { Background = Soft, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 10, 10), Child = sp };
    }

    public async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var r = await _client.SendAsync(new IpcRequest { Command = IpcCommands.Stats }, TimeSpan.FromSeconds(15));
            if (r.Ok && r.Stats != null) Render(r.Stats);
        }
        catch { /* the banner already says if the service is unreachable */ }
        finally { _loading = false; }
    }

    private void Render(StatsDto s)
    {
        Children.Clear();
        var head = new DockPanel { Margin = new Thickness(0, 6, 14, 10) };
        if (s.ProtectingSince is { } since)
        {
            var right = T($"NetGuard has been running since {since:dd.MM.yyyy}", 12, false, Muted);
            right.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(right, Dock.Right);
            head.Children.Add(right);
        }
        head.Children.Add(T("Your stats", 18, true));
        Children.Add(head);

        foreach (var g in s.Games) Children.Add(Card(Game(g)));

        // ---- streaks and counts
        var tiles = new UniformGrid { Columns = 4 };
        tiles.Children.Add(Tile("Days without an unlock request", s.DaysWithoutUnlockRequest.ToString(), s.DaysWithoutUnlockRequest >= 7 ? "great streak 👏" : null, "#2E7D6B"));
        tiles.Children.Add(Tile("Unlock requests (30 days)", s.UnlockRequests30Days.ToString(),
            s.UnlockRequestsTotal == 0 ? "none ever" : $"all time: {s.UnlocksConfirmed} confirmed, {s.UnlocksExpiredOrCancelled} let go"));
        tiles.Children.Add(Tile("Commitments (30 days)", s.Commitments30Days.ToString(), null));
        tiles.Children.Add(Tile("Firewall changes undone (30 days)", s.TamperUndone30Days.ToString(),
            s.EmergencyRestores > 0 ? $"emergency restores: {s.EmergencyRestores} (last {s.DaysSinceEmergencyRestore} days ago)" : "no emergency restores"));
        Children.Add(new Border { Margin = new Thickness(0, 0, 4, 4), Child = tiles });

        // ---- time per profile, last 7 days
        if (s.ProfileMinutes7Days.Count > 0)
        {
            var sp = new StackPanel();
            sp.Children.Add(T("Time per profile – last 7 days", 15, true, null, new Thickness(0, 0, 0, 10)));
            var max = s.ProfileMinutes7Days.Values.Max();
            foreach (var (profile, minutes) in s.ProfileMinutes7Days.OrderByDescending(k => k.Value))
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                row.Children.Add(T(profile, 13));
                var bar = new Border
                {
                    Height = 14, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left,
                    Background = Brush(Theme.ProfileColor(profile)), Width = Math.Max(3, 420.0 * minutes / max),
                };
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);
                var v = T(H(minutes), 12, false, Muted);
                Grid.SetColumn(v, 2);
                row.Children.Add(v);
                sp.Children.Add(row);
            }
            Children.Add(Card(sp));
        }
    }

    private static UIElement Game(GameStats g)
    {
        var sp = new StackPanel();
        sp.Children.Add(T($"🎮 {g.Profile}", 18, true, null, new Thickness(0, 0, 0, 10)));

        var tiles = new UniformGrid { Columns = 4 };
        var todayColor = g.DailyLimitMinutes is { } dl && g.TodayMinutes >= dl ? "#B03A2E" : "#1F4E79";
        tiles.Children.Add(Tile("Today", H(g.TodayMinutes), g.DailyLimitMinutes is { } d ? $"of {H(d)} allowed" : null, todayColor));
        tiles.Children.Add(Tile("This week", H(g.WeekMinutes), g.WeeklyLimitMinutes is { } w ? $"of {H(w)} allowed" : null, "#1F4E79"));
        tiles.Children.Add(Tile("This month", H(g.MonthMinutes), null, "#1F4E79"));
        tiles.Children.Add(Tile("All time", H(g.AllTimeMinutes), null, "#1F4E79"));
        sp.Children.Add(tiles);

        var facts = new List<string> { $"Ø {H(g.Average30DaysMinutes)} per day (last 30 days)" };
        if (g.Sessions30Days > 0) facts.Add($"{g.Sessions30Days} sessions, longest {H(g.LongestSessionMinutes)}");
        if (g.RunningAllTimeMinutes > 0) facts.Add($"game actually running: today {H(g.RunningTodayMinutes)}, this week {H(g.RunningWeekMinutes)}");
        sp.Children.Add(T(string.Join("  ·  ", facts), 12, false, Muted, new Thickness(0, 0, 0, 12)));

        // 30-day chart: profile active (light) with the time the game actually ran (dark) in front
        sp.Children.Add(T("Last 30 days", 13, true, null, new Thickness(0, 0, 0, 6)));
        var max = Math.Max(60, g.Last30Days.Max(x => Math.Max(x.Minutes, x.RunningMinutes)));
        if (g.DailyLimitMinutes is { } lim) max = Math.Max(max, lim);
        const double height = 110;
        var chart = new Grid { Height = height, Margin = new Thickness(0, 0, 0, 4) };
        var bars = new UniformGrid { Rows = 1, Columns = g.Last30Days.Count };
        foreach (var day in g.Last30Days)
        {
            var cell = new Grid { Margin = new Thickness(1, 0, 1, 0), Background = Brushes.Transparent };
            cell.Children.Add(new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(2, 2, 0, 0),
                Height = height * day.Minutes / max, Background = Brush(day.Date == DateTime.Today ? "#7FA6C9" : "#B9CCE0"),
            });
            cell.Children.Add(new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(2, 2, 0, 0), Margin = new Thickness(2, 0, 2, 0),
                Height = height * day.RunningMinutes / max, Background = Brush("#1F4E79"),
            });
            cell.ToolTip = $"{day.Date:ddd dd.MM.}: {H(day.Minutes)} in {g.Profile}" + (day.RunningMinutes > 0 ? $", game running {H(day.RunningMinutes)}" : "");
            bars.Children.Add(cell);
        }
        chart.Children.Add(bars);
        if (g.DailyLimitMinutes is { } limit)
            chart.Children.Add(new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom, Height = 1.5, Background = Brush("#B03A2E"), Opacity = 0.7,
                Margin = new Thickness(0, 0, 0, height * limit / max), ToolTip = $"daily limit {H(limit)}",
            });
        sp.Children.Add(chart);
        var axis = new DockPanel();
        var last = T(g.Last30Days[^1].Date.ToString("dd.MM."), 11, false, Muted);
        DockPanel.SetDock(last, Dock.Right);
        axis.Children.Add(last);
        axis.Children.Add(T(g.Last30Days[0].Date.ToString("dd.MM.") +
                            (g.DailyLimitMinutes != null ? "      red line = daily limit" : "") + "      dark = game actually running", 11, false, Muted));
        sp.Children.Add(axis);
        return sp;
    }
}
