using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NetGuard.App;

/// <summary>Small dialogs built in code (WPF has no input box).</summary>
public static class Dialogs
{
    private static Window NewWindow(Window owner, string title, double width) => new()
    {
        Title = title,
        Owner = owner,
        Width = width,
        SizeToContent = SizeToContent.Height,
        ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = Brushes.White,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 14,
        ShowInTaskbar = false,
    };

    private static TextBlock Text(string s, double size = 14, FontWeight? weight = null) => new()
    {
        Text = s, TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
        Margin = new Thickness(0, 0, 0, 12), Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x33)),
    };

    private static Button Btn(string content, bool primary, Window w) => new()
    {
        Content = content,
        Style = (Style)Application.Current.FindResource(primary ? "Primary" : "Secondary"),
        MinWidth = 100,
    };

    /// <summary>Asks for the recovery code. Returns null if cancelled.</summary>
    public static string? AskRecoveryCode(Window owner, string why)
    {
        var w = NewWindow(owner, "Recovery code", 520);
        var box = new TextBox
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 18, Padding = new Thickness(8),
            CharacterCasing = CharacterCasing.Upper, Margin = new Thickness(0, 0, 0, 16),
        };
        var ok = Btn("Continue", true, w);
        var cancel = Btn("Cancel", false, w);
        ok.IsDefault = true;
        cancel.IsCancel = true;
        ok.Click += (_, _) => { w.DialogResult = true; };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Text(why));
        panel.Children.Add(Text("Type your recovery code (dashes and spaces don't matter):"));
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        w.Content = panel;
        w.Loaded += (_, _) => Keyboard.Focus(box);
        return w.ShowDialog() == true && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    /// <summary>Shows a new recovery code once and makes the user retype its last group.</summary>
    public static void ShowNewRecoveryCode(Window owner, string code)
    {
        var w = NewWindow(owner, "Your recovery code", 620);
        var lastGroup = code.Split('-')[^1];
        var confirm = new TextBox
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 18, Padding = new Thickness(8), Width = 140,
            CharacterCasing = CharacterCasing.Upper, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16),
        };
        var done = Btn("I've written it down", true, w);
        done.IsEnabled = false;
        done.IsDefault = true;
        confirm.TextChanged += (_, _) => done.IsEnabled = string.Equals(confirm.Text.Trim(), lastGroup, StringComparison.OrdinalIgnoreCase);
        done.Click += (_, _) => { w.DialogResult = true; };
        w.Closing += (_, e) => { if (w.DialogResult != true) e.Cancel = !done.IsEnabled && MessageBox.Show(w,
            "This code will never be shown again. Close without writing it down?\n\n(You can create a new one later, but only while you have normal internet.)",
            "Recovery code", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes; };

        var codeBox = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8)), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 16),
            Child = new TextBlock
            {
                Text = code, FontFamily = new FontFamily("Consolas"), FontSize = 26, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Text("Write this code on paper now.", 18, FontWeights.SemiBold));
        panel.Children.Add(Text("It's shown only this once. Keep the paper somewhere inconvenient – another room, or with someone you trust. " +
                                "Don't save it on this PC, in a screenshot or in your browser."));
        panel.Children.Add(codeBox);
        panel.Children.Add(Text($"To confirm, type the last five characters of the code:"));
        panel.Children.Add(confirm);
        panel.Children.Add(done);
        w.Content = panel;
        w.Loaded += (_, _) => Keyboard.Focus(confirm);
        w.ShowDialog();
    }

    /// <summary>Asks for a short piece of text. Returns null if cancelled.</summary>
    public static string? AskText(Window owner, string title, string prompt)
    {
        var w = NewWindow(owner, title, 460);
        var box = new TextBox { FontSize = 16, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 16) };
        var ok = Btn("OK", true, w);
        var cancel = Btn("Cancel", false, w);
        ok.IsDefault = true;
        cancel.IsCancel = true;
        ok.Click += (_, _) => { w.DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Text(prompt));
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        w.Content = panel;
        w.Loaded += (_, _) => Keyboard.Focus(box);
        return w.ShowDialog() == true && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    /// <summary>Lists programs that are running right now (with a window or a known path) to pick one.</summary>
    public static string? PickRunningProgram(Window owner)
    {
        var items = new List<(string name, string path)>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path == null || path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) continue;
                var title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : $"{p.ProcessName} – {p.MainWindowTitle}";
                if (!items.Any(i => string.Equals(i.path, path, StringComparison.OrdinalIgnoreCase))) items.Add((title, path));
            }
            catch { /* processes of other users / protected ones */ }
            finally { p.Dispose(); }
        }
        items.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

        var w = NewWindow(owner, "Pick a running program", 640);
        var list = new ListBox { Height = 360, Margin = new Thickness(0, 0, 0, 16) };
        foreach (var (name, path) in items)
        {
            var sp = new StackPanel { Margin = new Thickness(4) };
            sp.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = path, FontSize = 12, Foreground = Brushes.Gray });
            list.Items.Add(new ListBoxItem { Content = sp, Tag = path });
        }
        var ok = Btn("Choose", true, w);
        var cancel = Btn("Cancel", false, w);
        ok.IsDefault = true;
        cancel.IsCancel = true;
        ok.Click += (_, _) => { if (list.SelectedItem != null) w.DialogResult = true; };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem != null) w.DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Text("Start the program (e.g. the game) first, then pick it here:"));
        panel.Children.Add(list);
        panel.Children.Add(buttons);
        w.Content = panel;
        return w.ShowDialog() == true ? (string)((ListBoxItem)list.SelectedItem).Tag : null;
    }

    /// <summary>Asks why the user wants to unlock. Returns the reason, or null if cancelled.</summary>
    public static string? AskUnlockReason(Window owner, string target, int requestsLeft)
    {
        const int min = 20;
        var w = NewWindow(owner, $"Request unlock: {target}", 560);
        var box = new TextBox { FontSize = 15, Padding = new Thickness(8), Height = 90, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, Margin = new Thickness(0, 0, 0, 6) };
        var counter = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 16) };
        var ok = Btn("Start the waiting time", true, w);
        var cancel = Btn("Never mind", false, w);
        ok.IsEnabled = false;
        cancel.IsCancel = true;
        box.TextChanged += (_, _) =>
        {
            var n = box.Text.Trim().Length;
            counter.Text = n < min ? $"{n} / {min} characters" : "✓";
            ok.IsEnabled = n >= min;
        };
        ok.Click += (_, _) => { w.DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Text($"Unlocking to {target} is a process, on purpose:", 16, FontWeights.SemiBold));
        panel.Children.Add(Text("1.  Write down why – honestly, for your future self (it shows up in the weekly summary).\n" +
                                "2.  A waiting time of 45 minutes starts. It can't be shortened.\n" +
                                "3.  Then you have 10 minutes to confirm by retyping a passage of about 400 characters.\n" +
                                "4.  If you don't confirm, the request expires. Nothing happens."));
        panel.Children.Add(Text($"This uses one of your unlock requests for today ({requestsLeft} left), even if you cancel it later."));
        panel.Children.Add(Text("Why do you want to unlock?", 14, FontWeights.SemiBold));
        panel.Children.Add(box);
        panel.Children.Add(counter);
        panel.Children.Add(buttons);
        w.Content = panel;
        w.Loaded += (_, _) => Keyboard.Focus(box);
        return w.ShowDialog() == true ? box.Text.Trim() : null;
    }

    /// <summary>Choose a profile (the current one or stricter) and an end time.</summary>
    public static (string profile, DateTime until)? AskCommitment(Window owner, NetGuard.Core.Ipc.StatusDto status)
    {
        var w = NewWindow(owner, "Commit", 560);
        var profiles = new ComboBox { FontSize = 14, Margin = new Thickness(0, 0, 0, 14), MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var p in status.Profiles.Where(p => p.Kind != "AllowAll" && (p.IsActive || p.SwitchAllowed)))
            profiles.Items.Add(p.Name);
        profiles.SelectedItem = status.Profiles.FirstOrDefault(p => p.IsActive && p.Kind != "AllowAll")?.Name ?? (profiles.Items.Count > 0 ? profiles.Items[0] : null);

        var start = status.CommitmentUntilUtc?.ToLocalTime().DateTime is { } cu && cu > DateTime.Now ? cu : DateTime.Now;
        var date = new DatePicker { SelectedDate = start.Date, DisplayDateStart = DateTime.Today, DisplayDateEnd = DateTime.Today.AddDays(30), Width = 150, Margin = new Thickness(0, 0, 8, 0) };
        var time = new ComboBox { Width = 90 };
        for (var m = 0; m < 24 * 60; m += 15) time.Items.Add($"{m / 60:00}:{m % 60:00}");
        void Set(DateTime t)
        {
            date.SelectedDate = t.Date;
            var rounded = (int)Math.Ceiling(t.TimeOfDay.TotalMinutes / 15.0) * 15;
            if (rounded >= 1440) { date.SelectedDate = t.Date.AddDays(1); rounded = 0; }
            time.SelectedItem = $"{rounded / 60:00}:{rounded % 60:00}";
        }
        Set(start.AddHours(2));
        var when = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        when.Children.Add(date);
        when.Children.Add(time);

        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        void Preset(string label, Func<DateTime> t)
        {
            var b = Btn(label, false, w);
            b.Padding = new Thickness(10, 4, 10, 4);
            b.FontSize = 12;
            b.Click += (_, _) => Set(t());
            presets.Children.Add(b);
        }
        Preset("+1 hour", () => DateTime.Now.AddHours(1));
        Preset("+3 hours", () => DateTime.Now.AddHours(3));
        Preset("today 23:00", () => DateTime.Today.AddHours(23));
        Preset("tomorrow 08:00", () => DateTime.Today.AddDays(1).AddHours(8));
        Preset("tomorrow 18:00", () => DateTime.Today.AddDays(1).AddHours(18));
        Preset("+1 week", () => DateTime.Now.AddDays(7));

        var ok = Btn("Commit", true, w);
        var cancel = Btn("Cancel", false, w);
        cancel.IsCancel = true;
        ok.Click += (_, _) => { if (profiles.SelectedItem != null && date.SelectedDate != null && time.SelectedItem != null) w.DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Text("A commitment is a promise to your future self: until the time you choose, nothing looser than this profile " +
                                "is possible – no unlock requests. Stricter is always fine. It can be extended, but never shortened."));
        if (status.CommitmentUntilUtc is { } c)
            panel.Children.Add(Text($"Current commitment: {status.CommitmentProfile} until {c.ToLocalTime():dddd HH:mm}. A new one must last at least as long.", 13, FontWeights.SemiBold));
        panel.Children.Add(Text("Stay in", 14, FontWeights.SemiBold));
        panel.Children.Add(profiles);
        panel.Children.Add(Text("until", 14, FontWeights.SemiBold));
        panel.Children.Add(when);
        panel.Children.Add(presets);
        panel.Children.Add(buttons);
        w.Content = panel;
        if (w.ShowDialog() != true) return null;
        var parts = ((string)time.SelectedItem).Split(':');
        var until = date.SelectedDate!.Value.Date.AddHours(int.Parse(parts[0])).AddMinutes(int.Parse(parts[1]));
        return ((string)profiles.SelectedItem!, until);
    }
}
