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
}
