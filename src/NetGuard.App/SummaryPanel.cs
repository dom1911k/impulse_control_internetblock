using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetGuard.Core.Ipc;
using NetGuard.Core.Reporting;
using NetGuard.Windows;

namespace NetGuard.App;

/// <summary>The weekly summary at the top of the History tab: play time, time per profile, unlock reasons.</summary>
public sealed class SummaryPanel : Border
{
    private readonly GuardClient _client;
    private readonly StackPanel _body = new();
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _next;
    private int _offset;

    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x5F, 0x6B, 0x7A));
    private static readonly SolidColorBrush Ink = new(Color.FromRgb(0x1F, 0x29, 0x33));

    public SummaryPanel(GuardClient client)
    {
        _client = client;
        Background = Brushes.White;
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xE3, 0xEA));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(18);
        Margin = new Thickness(0, 0, 14, 14);

        var prev = Nav("←", -1);
        _next = Nav("→", +1);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        nav.Children.Add(prev);
        nav.Children.Add(_next);
        DockPanel.SetDock(nav, Dock.Right);
        head.Children.Add(nav);
        head.Children.Add(_title);

        var root = new StackPanel();
        root.Children.Add(head);
        root.Children.Add(_body);
        Child = root;
    }

    private Button Nav(string text, int delta)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.FindResource("Secondary"), Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6, 0, 0, 0) };
        b.Click += async (_, _) => { _offset = Math.Min(0, _offset + delta); await RefreshAsync(); };
        return b;
    }

    private static TextBlock T(string s, double size = 13, bool bold = false, Brush? color = null) => new()
    {
        Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = color ?? Ink,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
    };

    private static string H(int minutes) => minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}min" : $"{minutes}min";

    public async Task RefreshAsync()
    {
        _next.IsEnabled = _offset < 0;
        SummaryDto? s;
        try
        {
            var r = await _client.SendAsync(new IpcRequest { Command = IpcCommands.Summary, WeekOffset = _offset });
            s = r.Summary;
            if (!r.Ok || s == null) { _body.Children.Clear(); _body.Children.Add(T(r.Error ?? "No summary.", 13, false, Muted)); return; }
        }
        catch (Exception e) { _body.Children.Clear(); _body.Children.Add(T("Couldn't load the summary: " + e.Message, 13, false, Muted)); return; }

        _title.Text = (_offset == 0 ? "This week" : _offset == -1 ? "Last week" : $"{-_offset} weeks ago") +
                      $"  ·  {s.WeekStart:ddd dd.MM.} – {s.WeekEnd:ddd dd.MM.}";
        _body.Children.Clear();

        // play time
        foreach (var p in s.Play)
        {
            _body.Children.Add(T($"🎮 {p.Profile}: {H(p.TotalMinutes)}" + (p.WeeklyLimitMinutes is { } w ? $" of {H(w)}" : ""), 14, true));
            var max = Math.Max(30, Math.Max(p.DailyLimitMinutes ?? 0, p.MinutesPerDay.DefaultIfEmpty(0).Max()));
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            var days = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
            for (var i = 0; i < 7; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = T(days[i], 12, false, Muted);
                Grid.SetRow(label, i);
                grid.Children.Add(label);
                var bar = new Border
                {
                    Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2),
                    Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x79)),
                    Width = Math.Max(p.MinutesPerDay[i] > 0 ? 3 : 0, 360.0 * p.MinutesPerDay[i] / max),
                };
                Grid.SetRow(bar, i);
                Grid.SetColumn(bar, 1);
                grid.Children.Add(bar);
                var value = T(p.MinutesPerDay[i] > 0 ? H(p.MinutesPerDay[i]) : "–", 12, false, Muted);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 2);
                grid.Children.Add(value);
            }
            _body.Children.Add(grid);
        }

        // time per profile
        if (s.MinutesPerProfile.Count > 0)
            _body.Children.Add(T("⏱ Time per profile: " + string.Join("  ·  ", s.MinutesPerProfile.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {H(k.Value)}")), 13));

        // unlock reasons
        _body.Children.Add(T(s.Unlocks.Count == 0 ? "🔓 No unlock requests. 👍" : $"🔓 Unlock requests: {s.Unlocks.Count}", 14, true));
        foreach (var u in s.Unlocks)
            _body.Children.Add(T($"   {u.RequestedLocal:ddd HH:mm} → {u.Target} ({u.Outcome}): \"{u.Reason}\"", 12));

        // the rest
        var other = new List<string>();
        if (s.Commitments > 0) other.Add($"{s.Commitments} commitment(s)");
        if (s.ExtraMinutesTaken > 0) other.Add($"extra minutes taken {s.ExtraMinutesTaken}×");
        if (s.AutoSwitches > 0) other.Add($"{s.AutoSwitches} automatic switch(es)");
        if (s.TamperUndone > 0) other.Add($"{s.TamperUndone} change(s) to the firewall undone");
        if (s.WrongCodes > 0) other.Add($"{s.WrongCodes} wrong recovery code(s)");
        if (s.EmergencyRestores > 0) other.Add($"{s.EmergencyRestores} emergency restore(s)");
        if (s.ClockChanges > 0) other.Add($"{s.ClockChanges} clock change(s) ignored");
        if (other.Count > 0) _body.Children.Add(T("📋 " + string.Join("  ·  ", other), 13, false, Muted));
    }
}
