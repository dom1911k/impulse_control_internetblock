using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetGuard.Core.Ipc;
using NetGuard.Windows;

namespace NetGuard.App;

/// <summary>
/// The program editor: which programs each profile lets online, and which are always blocked.
/// Removing is always possible; adding (letting more online) only with normal internet or in Maintenance.
/// </summary>
public sealed class ProgramsPanel : Grid
{
    private const string AlwaysBlocked = "Always blocked";
    private readonly Window _owner;
    private readonly GuardClient _client;
    private readonly ListBox _lists = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 15 };
    private readonly StackPanel _detail = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = Muted };
    private ConfigDto? _config;
    private string? _selected;

    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x5F, 0x6B, 0x7A));
    private static readonly SolidColorBrush Ink = new(Color.FromRgb(0x1F, 0x29, 0x33));
    private static readonly SolidColorBrush Line = new(Color.FromRgb(0xDD, 0xE3, 0xEA));

    public ProgramsPanel(Window owner, GuardClient client)
    {
        _owner = owner;
        _client = client;
        Margin = new Thickness(4, 14, 0, 0);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // left: profiles + always blocked
        var left = new DockPanel { Margin = new Thickness(0, 0, 16, 0) };
        var leftButtons = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        leftButtons.Children.Add(Btn("New profile…", false, async () => await NewProfileAsync()));
        leftButtons.Children.Add(Btn("Delete profile…", false, async () => await DeleteProfileAsync()));
        left.Children.Add(leftButtons);
        left.Children.Add(new Border { Background = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Child = _lists });
        _lists.SelectionChanged += (_, _) => { if (_lists.SelectedItem is ListBoxItem i) { _selected = (string)i.Tag; ShowDetail(); } };
        SetColumn(left, 0);
        Children.Add(left);

        var right = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _detail };
        SetColumn(right, 1);
        Children.Add(right);
    }

    private Button Btn(string text, bool primary, Func<Task> onClick)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.FindResource(primary ? "Primary" : "Secondary"), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += async (_, _) => await onClick();
        return b;
    }

    public async Task RefreshAsync()
    {
        try
        {
            var r = await _client.SendAsync(new IpcRequest { Command = IpcCommands.ConfigGet });
            if (!r.Ok) { ShowMessage(r.Error ?? "Could not load the programs."); return; }
            _config = r.Config;
        }
        catch (Exception e) { ShowMessage("Could not load the programs: " + e.Message); return; }

        _lists.Items.Clear();
        foreach (var p in _config!.Profiles)
            _lists.Items.Add(new ListBoxItem { Content = p.Name + (p.IsActive ? "   (active)" : ""), Tag = p.Name, Padding = new Thickness(8, 6, 8, 6) });
        _lists.Items.Add(new ListBoxItem { Content = AlwaysBlocked, Tag = AlwaysBlocked, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 8, 0, 0) });
        _selected ??= _config.Profiles.FirstOrDefault(p => p.Kind == "Allowlist" && p.Programs.Count > 0)?.Name ?? AlwaysBlocked;
        var item = _lists.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == _selected) ?? (ListBoxItem)_lists.Items[0];
        _lists.SelectedItem = item;
        ShowDetail();
    }

    private void ShowMessage(string text)
    {
        _detail.Children.Clear();
        _detail.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Muted });
    }

    private void ShowDetail()
    {
        if (_config == null || _selected == null) return;
        var isBlocked = _selected == AlwaysBlocked;
        var p = isBlocked ? _config.AlwaysBlocked : _config.Profiles.FirstOrDefault(x => x.Name == _selected);
        if (p == null) return;
        _detail.Children.Clear();

        _detail.Children.Add(new TextBlock { Text = p.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Ink });
        _detail.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 4, 0, 14),
            Text = isBlocked ? "These programs never get internet, in any profile – even in Maintenance."
                : p.Kind == "AllowAll" ? "Maintenance lets everything online except the always-blocked programs. Nothing to choose here."
                : "Only these programs (plus DNS and time sync) can use the internet in this profile.",
        });
        if (!isBlocked && p.Kind == "AllowAll") return;

        // add buttons
        var canAdd = isBlocked || _config.CanLoosen;
        var add = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var addFile = Btn("Add program file…", true, async () => await AddFromFileAsync());
        var addRunning = Btn("Add a running program…", false, async () => await AddRunningAsync());
        addFile.IsEnabled = addRunning.IsEnabled = canAdd;
        add.Children.Add(addFile);
        add.Children.Add(addRunning);
        if (!isBlocked)
        {
            var name = p.Name;
            add.Children.Add(Btn("Preview firewall changes", false, async () => await PreviewAsync(name)));
        }
        _detail.Children.Add(add);
        if (!canAdd)
            _detail.Children.Add(new TextBlock { Text = _config.LoosenHint, TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 10) });
        _detail.Children.Add(_status);

        if (p.Programs.Count == 0)
            _detail.Children.Add(new TextBlock { Text = "No programs yet.", Foreground = Muted, Margin = new Thickness(0, 12, 0, 0) });

        foreach (var prog in p.Programs)
        {
            var card = new StackPanel();
            var head = new DockPanel();
            var canRemove = !isBlocked || _config.CanLoosen;
            var remove = Btn("Remove", false, async () => await RemoveAsync(prog.Name));
            remove.IsEnabled = canRemove;
            remove.Margin = new Thickness(8, 0, 0, 0);
            if (!canRemove) remove.ToolTip = _config.LoosenHint;
            DockPanel.SetDock(remove, Dock.Right);
            head.Children.Add(remove);
            if (!isBlocked && prog.Pin == "Sha256" && prog.Files.Any(f => !f.Allowed))
            {
                var name = prog.Name;
                var re = Btn("Re-approve new version", true, async () => await ReapproveAsync(name));
                re.IsEnabled = _config.CanLoosen;
                re.ToolTip = _config.CanLoosen ? "The program changed (probably an update). Accept the new version." : _config.LoosenHint;
                re.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(re, Dock.Right);
                head.Children.Add(re);
            }
            head.Children.Add(new TextBlock { Text = prog.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center });
            card.Children.Add(head);
            card.Children.Add(new TextBlock { Text = prog.PinDescription, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 6) });

            if (prog.Files.Count == 0)
                card.Children.Add(new TextBlock { Text = "⚠ No file found at: " + string.Join("; ", prog.Paths), TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.DarkOrange });
            foreach (var f in prog.Files.Take(12))
                card.Children.Add(new TextBlock
                {
                    Text = (f.Allowed ? (isBlocked ? "⛔ " : "✅ ") : "❌ ") + f.Path + (f.Allowed ? "" : $"  –  {f.Detail}"),
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, FontFamily = new FontFamily("Consolas"),
                    Foreground = f.Allowed ? Ink : Brushes.Firebrick,
                });
            if (prog.Files.Count > 12)
                card.Children.Add(new TextBlock { Text = $"… and {prog.Files.Count - 12} more files", FontSize = 12, Foreground = Muted });

            _detail.Children.Add(new Border
            {
                Background = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14), Margin = new Thickness(0, 10, 14, 0), Child = card,
            });
        }
    }

    private async Task AddFromFileAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose a program for {_selected}",
            Filter = "Programs (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        if (dlg.ShowDialog(_owner) == true) await AddAsync(dlg.FileName);
    }

    private async Task AddRunningAsync()
    {
        var path = Dialogs.PickRunningProgram(_owner);
        if (path != null) await AddAsync(path);
    }

    private async Task AddAsync(string path)
    {
        try
        {
            var info = await _client.CallAsync(new IpcRequest { Command = IpcCommands.InspectFile, Path = path });
            var prog = info.Program!;
            var verb = _selected == AlwaysBlocked ? "block" : "allow";
            if (MessageBox.Show(_owner, $"{(verb == "block" ? "Always block" : $"Allow in {_selected}")}: {prog.Name}?\n\n{path}\n\n{prog.PinDescription}",
                    "Add program", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var r = await _client.CallAsync(new IpcRequest { Command = IpcCommands.ProgramAdd, Profile = _selected, Path = path });
            _status.Text = r.Text;
        }
        catch (Exception e) { _status.Text = e.Message; }
        var keep = _status.Text;
        await RefreshAsync();
        _status.Text = keep;
    }

    private async Task RemoveAsync(string entry)
    {
        var where = _selected == AlwaysBlocked ? "the always-blocked list" : _selected;
        if (MessageBox.Show(_owner, $"Remove {entry} from {where}?", "Remove program", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { _status.Text = (await _client.CallAsync(new IpcRequest { Command = IpcCommands.ProgramRemove, Profile = _selected, Entry = entry })).Text; }
        catch (Exception e) { _status.Text = e.Message; }
        var keep = _status.Text;
        await RefreshAsync();
        _status.Text = keep;
    }

    private async Task ReapproveAsync(string entry)
    {
        if (MessageBox.Show(_owner, $"{entry} changed since you added it. Only re-approve it if you know why (e.g. it updated itself).\n\nApprove the current version?",
                "Re-approve", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { _status.Text = (await _client.CallAsync(new IpcRequest { Command = IpcCommands.ProgramReapprove, Profile = _selected, Entry = entry })).Text; }
        catch (Exception e) { _status.Text = e.Message; }
        var keep = _status.Text;
        await RefreshAsync();
        _status.Text = keep;
    }

    private async Task PreviewAsync(string profile)
    {
        try
        {
            var r = await _client.CallAsync(new IpcRequest { Command = IpcCommands.Preview, Profile = profile });
            Dialogs.ShowText(_owner, $"Preview: {profile}", r.Text ?? "");
        }
        catch (Exception e) { _status.Text = e.Message; }
    }

    private async Task NewProfileAsync()
    {
        var name = Dialogs.AskText(_owner, "New profile", "Name of the new profile (e.g. \"Music\" or the name of a game):");
        if (name == null) return;
        try
        {
            await _client.CallAsync(new IpcRequest { Command = IpcCommands.ProfileAdd, Profile = name });
            _selected = name.Trim();
        }
        catch (Exception e) { MessageBox.Show(_owner, e.Message, "New profile", MessageBoxButton.OK, MessageBoxImage.Warning); }
        await RefreshAsync();
    }

    private async Task DeleteProfileAsync()
    {
        if (_selected == null || _selected == AlwaysBlocked) return;
        if (MessageBox.Show(_owner, $"Delete the profile {_selected}?", "Delete profile", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            await _client.CallAsync(new IpcRequest { Command = IpcCommands.ProfileRemove, Profile = _selected });
            _selected = null;
        }
        catch (Exception e) { MessageBox.Show(_owner, e.Message, "Delete profile", MessageBoxButton.OK, MessageBoxImage.Warning); }
        await RefreshAsync();
    }
}
