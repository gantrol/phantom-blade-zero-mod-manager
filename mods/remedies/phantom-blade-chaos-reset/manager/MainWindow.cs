using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhantomBladeQuestManager;

sealed class MainWindow : Window
{
    readonly ComboBox saveFiles = new() { MinWidth = 360, IsEditable = false, Margin = new(0, 0, 8, 0) };
    readonly ListBox slots = new() { MinWidth = 250, Margin = new(0, 0, 24, 0) };
    readonly StackPanel quests = new();
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16), FontWeight = FontWeights.SemiBold };
    readonly TextBlock saveDetail = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 20) };
    readonly Button reset = new() { MinWidth = 88, IsEnabled = false };
    readonly Button restore = new() { Margin = new(8, 0, 0, 0), IsEnabled = false };
    readonly Button manualCopy = CreateIcon("\uE74E", "manualCopy");
    readonly List<CheckBox> choices = [];
    readonly Grid content = new() { Margin = new(24) };
    SaveService? service;
    SaveInfo? current;
    bool busy;
    bool hasBackup;

    public MainWindow()
    {
        Title = Locale.Get("title");
        Width = Math.Min(920, SystemParameters.WorkArea.Width); Height = Math.Min(570, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(760, Width); MinHeight = Math.Min(460, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        Background = SystemColors.WindowBrush; Foreground = SystemColors.WindowTextBrush;
        if (!SystemParameters.HighContrast)
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("GameTheme.xaml", UriKind.Relative) });
            Background = (Brush)Resources["Ink"]; Foreground = (Brush)Resources["Paper"];
        }
        for (int i = 0; i < 4; i++) content.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Content = content;
        var toolbar = new DockPanel { Margin = new(0, 0, 0, 24) };
        var browse = CreateIcon("\uE8B7", "browse"); browse.Click += (_, _) => Browse();
        var refresh = CreateIcon("\uE72C", "refresh"); refresh.Click += async (_, _) => await RefreshAsync();
        DockPanel.SetDock(browse, Dock.Right); toolbar.Children.Add(browse);
        DockPanel.SetDock(refresh, Dock.Right); toolbar.Children.Add(refresh);
        toolbar.Children.Add(saveFiles); content.Children.Add(toolbar);
        AutomationProperties.SetName(saveFiles, Locale.Get("saveFile"));
        saveFiles.SelectionChanged += async (_, _) => { if (!busy) await RefreshAsync(); };
        Grid.SetRow(status, 1); content.Children.Add(status);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) }); body.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(body, 2); content.Children.Add(body);
        AutomationProperties.SetName(slots, Locale.Get("slots"));
        body.Children.Add(slots); slots.SelectionChanged += (_, _) => RenderQuests();
        var detailPane = new Grid();
        detailPane.RowDefinitions.Add(new RowDefinition());
        detailPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(detailPane, 1); body.Children.Add(detailPane);
        var detail = new StackPanel();
        var detailScroll = new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        detailPane.Children.Add(detailScroll);
        detail.Children.Add(saveDetail); detail.Children.Add(quests);
        var commands = new WrapPanel { Margin = new(0, 24, 0, 0) };
        reset.Content = Locale.Get("reset"); reset.Padding = new(16, 7, 16, 7);
        restore.Content = Locale.Get("restore"); restore.Padding = new(12, 7, 12, 7);
        reset.Click += async (_, _) => await SaveManualAsync(true); restore.Click += async (_, _) => await RestoreAsync();
        manualCopy.Click += async (_, _) => await SaveManualAsync(false);
        commands.Children.Add(reset); commands.Children.Add(restore);
        commands.Children.Add(manualCopy);
        var backups = CreateIcon("\uED25", "backups"); backups.Margin = new(12, 0, 0, 0);
        backups.Click += (_, _) =>
        {
            if (current == null) return;
            string folder = SaveService.BackupDirectory(current.Path);
            if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        };
        commands.Children.Add(backups); Grid.SetRow(commands, 1); detailPane.Children.Add(commands);
        var note = new TextBlock { Text = Locale.Get("backupNote"), Margin = new(0, 24, 0, 0), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(note, 3); content.Children.Add(note);
        Loaded += async (_, _) => await WorkAsync(async () =>
        {
            service = await Task.Run(() => Program.CreateServiceAsync());
            string[] paths = await Task.Run(() => GameFiles.FindSaves(service.Game));
            foreach (string path in paths) saveFiles.Items.Add(path);
            if (paths.Length == 0) { status.Text = Locale.Get("saveMissing"); return; }
            saveFiles.SelectedIndex = 0;
            await LoadSelectedAsync();
        });
    }

    static Button CreateIcon(string glyph, string key)
    {
        var button = new Button { Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Padding = new(10), Margin = new(4, 0, 0, 0), ToolTip = Locale.Get(key) };
        AutomationProperties.SetName(button, Locale.Get(key)); return button;
    }

    async Task WorkAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; content.IsEnabled = false; status.ToolTip = null; status.Text = Locale.Get("working");
        try { await action(); }
        catch (Exception error) { status.Text = Locale.Error(error); }
        finally { busy = false; content.IsEnabled = true; UpdateActions(); }
    }

    void Browse()
    {
        var dialog = new OpenFileDialog { Title = Locale.Get("browse"), Filter = Locale.Get("saveFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        string path = dialog.FileName;
        if (!saveFiles.Items.Contains(path)) saveFiles.Items.Add(path);
        saveFiles.SelectedItem = path;
    }

    async Task RefreshAsync() => await WorkAsync(() => LoadSelectedAsync());

    async Task LoadSelectedAsync(string? selectSlot = null)
    {
        if (service == null || saveFiles.SelectedItem is not string path) return;
        string? previous = selectSlot ?? (slots.SelectedItem as ListBoxItem)?.Tag as string;
        current = null; slots.Items.Clear(); RenderQuests();
        current = await Task.Run(() => service.InspectAsync(path));
        hasBackup = await Task.Run(() => SaveService.LatestBackup(path) != null);
        foreach (var slot in current.Slots)
        {
            var label = new StackPanel { Margin = new(8, 6, 8, 6) };
            label.Children.Add(new TextBlock { Text = Locale.Slot(slot.Name), FontWeight = FontWeights.SemiBold });
            label.Children.Add(new TextBlock { Text = Locale.SaveDate(slot.Date), FontSize = 12, Margin = new(0, 4, 0, 0) });
            slots.Items.Add(new ListBoxItem { Content = label, Tag = slot.Name });
        }
        slots.SelectedItem = slots.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == previous) ?? slots.Items.OfType<ListBoxItem>().FirstOrDefault();
        if (slots.SelectedItem != null) slots.ScrollIntoView(slots.SelectedItem);
        status.Text = Locale.Get("loaded", current.Slots.Length);
    }

    SlotInfo? Selected => current?.Slots.SingleOrDefault(s => s.Name == ((slots.SelectedItem as ListBoxItem)?.Tag as string));

    void RenderQuests()
    {
        quests.Children.Clear(); choices.Clear(); saveDetail.Text = "";
        if (Selected is not SlotInfo slot) { UpdateActions(); return; }
        saveDetail.Text = Locale.Get("slotDetail", Locale.Slot(slot.Name), (slot.PlaySeconds / 3600.0).ToString("0.0"));
        foreach (var quest in slot.Quests)
        {
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = Locale.Get(quest.Id), FontSize = 19, FontFamily = new FontFamily("SimSun") });
            label.Children.Add(new TextBlock { Text = Locale.Get(quest.Status), TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) });
            if (quest.Id == "cultDaughter")
            {
                label.Margin = new(18, 0, 0, 24); quests.Children.Add(label); continue;
            }
            var choice = new CheckBox { Content = label, Tag = quest.Id, IsEnabled = quest.Eligible, Foreground = Foreground, Margin = new(0, 0, 0, 24), VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(choice, Locale.Get(quest.Id) + ": " + Locale.Get(quest.Status));
            choice.Checked += (_, _) => UpdateActions(); choice.Unchecked += (_, _) => UpdateActions();
            choices.Add(choice); quests.Children.Add(choice);
        }
        UpdateActions();
    }

    void UpdateActions()
    {
        reset.IsEnabled = !busy && current != null && choices.Any(c => c.IsChecked == true && c.IsEnabled);
        restore.IsEnabled = !busy && current != null && hasBackup;
        manualCopy.IsEnabled = !busy && Selected != null;
    }

    async Task SaveManualAsync(bool resetQuests) => await WorkAsync(async () =>
    {
        if (service == null || current == null || Selected == null) return;
        string slot = Selected.Name;
        string[] ids = resetQuests ? choices.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray() : [];
        var result = await Task.Run(() => service.CreateManualAsync(current.Path, current.Fingerprint, slot, ids));
        await LoadSelectedAsync(result.Slot);
        status.Text = Locale.Get(ids.Contains("sevenStar") ? "sevenStarDone" : ids.Contains("stoneBracer") ? "bracerDone" : resetQuests ? "resetDone" : "manualDone", Locale.Slot(result.Slot));
        if (ids.Contains("sevenStar") && ids.Contains("stoneBracer")) status.Text += Environment.NewLine + Locale.Get("bracerDone", Locale.Slot(result.Slot));
        status.ToolTip = result.Backup;
    });

    async Task RestoreAsync() => await WorkAsync(async () =>
    {
        if (service == null || current == null) return;
        await Task.Run(() => service.RestoreAsync(current.Path, current.Fingerprint));
        await LoadSelectedAsync(); status.Text = Locale.Get("restoreDone");
    });
}
