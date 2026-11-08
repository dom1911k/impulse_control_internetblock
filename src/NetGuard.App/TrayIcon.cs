using NetGuard.Core.Ipc;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace NetGuard.App;

/// <summary>
/// The tray icon: its colour shows the state (green = normal internet, slate = Offline, blue = WoW,
/// amber = Maintenance, red = service not running); hovering shows the details.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly StatusMonitor _monitor;
    private readonly Dictionary<string, Drawing.Icon> _icons = new();
    private DateTimeOffset? _lastTamperSeen;
    private bool _first = true;
    private bool _wasDown;
    private DateTimeOffset? _warnedDeadline;

    public TrayIcon(StatusMonitor monitor, Action open, Action exit)
    {
        _monitor = monitor;
        _statusItem = new Forms.ToolStripMenuItem("…") { Enabled = false };
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Open NetGuard", null, (_, _) => open());
        openItem.Font = new Drawing.Font(openItem.Font, Drawing.FontStyle.Bold);
        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Hide tray icon (protection keeps running)", null, (_, _) => exit()));

        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "NetGuard" };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => open();
        _monitor.Changed += Update;
        Update();
    }

    private void Update()
    {
        var s = _monitor.Status;
        string text, color;
        if (!_monitor.HasLoaded) { text = "NetGuard – starting…"; color = Theme.NormalInternet; }
        else if (s == null) { text = "NetGuard – protection service not reachable"; color = "#B03A2E"; }
        else if (s.ActiveProfile == null) { text = "NetGuard – normal internet"; color = Theme.NormalInternet; }
        else
        {
            text = s.TestDeadlineUtc is { } d
                ? $"NetGuard – {s.ActiveProfile} test lock until {d.ToLocalTime():t}"
                : $"NetGuard – {s.ActiveProfile} mode";
            color = Theme.ProfileColor(s.ActiveProfile);
        }
        _icon.Text = text.Length > 127 ? text[..127] : text;
        _icon.Icon = IconFor(color);
        _statusItem.Text = text.Replace("NetGuard – ", "");

        if (_monitor.HasLoaded) Notify(s);
    }

    private void Notify(StatusDto? s)
    {
        if (_first) { _first = false; _lastTamperSeen = s?.LastTamperUtc; _wasDown = s == null; return; }

        if (s == null && !_wasDown)
            _icon.ShowBalloonTip(8000, "NetGuard", "Can't reach the protection service. Open NetGuard for details.", Forms.ToolTipIcon.Warning);
        _wasDown = s == null;
        if (s == null) return;

        if (s.LastTamperUtc != null && s.LastTamperUtc != _lastTamperSeen)
            _icon.ShowBalloonTip(6000, "NetGuard", "A change to the firewall was noticed and undone. See History.", Forms.ToolTipIcon.Info);
        _lastTamperSeen = s.LastTamperUtc;

        if (s.TestDeadlineUtc is { } d && d - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(60) && _warnedDeadline != d)
        {
            _warnedDeadline = d;
            _icon.ShowBalloonTip(5000, "NetGuard", "The test lock ends in less than a minute.", Forms.ToolTipIcon.Info);
        }
    }

    /// <summary>A small shield in the state's colour with a white padlock.</summary>
    private Drawing.Icon IconFor(string hex)
    {
        if (_icons.TryGetValue(hex, out var cached)) return cached;
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var fill = Drawing.ColorTranslator.FromHtml(hex);
            Drawing.PointF[] shield =
            {
                new(16, 1), new(29, 5), new(29, 15), new(25, 24), new(16, 31), new(7, 24), new(3, 15), new(3, 5),
            };
            using (var b = new Drawing.SolidBrush(fill)) g.FillPolygon(b, shield);
            using var white = new Drawing.SolidBrush(Drawing.Color.White);
            using var pen = new Drawing.Pen(Drawing.Color.White, 2.4f);
            g.DrawArc(pen, 12, 8, 8, 9, 180, 180);
            g.FillRectangle(white, 10.5f, 14, 11, 9);
        }
        var icon = Drawing.Icon.FromHandle(bmp.GetHicon());
        _icons[hex] = icon;
        return icon;
    }

    public void Dispose()
    {
        _monitor.Changed -= Update;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
