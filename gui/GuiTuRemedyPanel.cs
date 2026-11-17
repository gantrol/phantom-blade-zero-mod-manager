namespace PhantomBladeModManager;

internal sealed class GuiTuRemedyPanel : UserControl
{
    private readonly ManagerClient client;
    private readonly QuestRemedyClient service;
    private readonly Func<Func<Task>, Task> run;
    private readonly ComboBox files = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Label" };
    private readonly ComboBox slots = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Label" };
    private readonly InkLabel title = new() { Dock = DockStyle.Fill, Font = GameTheme.TitleFont(13f), TextAlign = ContentAlignment.MiddleLeft };
    private readonly InkLabel status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly InkLabel note = new() { Dock = DockStyle.Fill, Font = GameTheme.BaseFont(8f), ForeColor = GameTheme.Muted };
    private readonly TextActionButton repair = new() { Dock = DockStyle.Fill, Accent = true };
    private readonly IconButton browse = new(GameIcon.Folder, 32, 30, 18f);
    private readonly IconButton refresh = new(GameIcon.Refresh, 32, 30, 18f);
    private readonly ToolTip tips = new();
    private string? game;
    private RemedySave? current;
    private bool loading, busy, running, initialized;
    private sealed record FileChoice(string Path)
    {
        public string Label => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)) + " · " + System.IO.Path.GetFileName(Path);
    }
    private sealed record SlotChoice(RemedySlot Slot, string Label);

    public GuiTuRemedyPanel(string root, ManagerClient client, Func<Func<Task>, Task> run)
    {
        this.client = client;
        this.run = run;
        service = new(root, client);
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 0, 0, 10);
        var frame = new BorderedPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 12, 6) };
        var layout = new InkLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 4, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.Controls.Add(title, 0, 0); layout.SetColumnSpan(title, 5);
        layout.Controls.Add(files, 0, 1); layout.Controls.Add(slots, 1, 1);
        layout.Controls.Add(repair, 2, 1); layout.Controls.Add(browse, 3, 1); layout.Controls.Add(refresh, 4, 1);
        layout.Controls.Add(status, 0, 2); layout.SetColumnSpan(status, 5);
        layout.Controls.Add(note, 0, 3); layout.SetColumnSpan(note, 5);
        frame.Controls.Add(layout); Controls.Add(frame);
        files.SelectedIndexChanged += async (_, _) => { if (!loading && !busy) await run(() => ReadSelectedAsync()); };
        slots.SelectedIndexChanged += (_, _) => UpdateState();
        refresh.Click += async (_, _) => { if (game != null) await run(() => LoadAsync(game)); };
        browse.Click += async (_, _) => await BrowseAsync();
        repair.Click += async (_, _) => await run(RepairAsync);
        Disposed += (_, _) => tips.Dispose();
        foreach (var combo in new[] { files, slots })
        {
            combo.FlatStyle = FlatStyle.Flat;
            combo.BackColor = SystemInformation.HighContrast ? SystemColors.Window : GameTheme.Surface;
            combo.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : GameTheme.Paper;
        }
    }

    public void SetContext(bool gameRunning, bool hostBusy)
    {
        running = gameRunning; busy = hostBusy; UpdateState();
    }

    public async Task LoadAsync(string gamePath)
    {
        bool changedGame = game != gamePath;
        game = gamePath;
        title.Text = client.Text("gui_guiTuRegret");
        if (!service.Available) { status.Text = client.Text("gui_questToolMissing"); return; }
        if (!initialized)
        {
            await service.InitializeAsync(); initialized = true;
            title.Text = service.Text("guiTu");
            note.Text = service.Text("guiTuPanelNote");
            files.AccessibleName = service.Text("saveFile"); slots.AccessibleName = service.Text("slots");
            browse.AccessibleName = service.Text("browse"); refresh.AccessibleName = service.Text("refresh");
            tips.SetToolTip(browse, browse.AccessibleName); tips.SetToolTip(refresh, refresh.AccessibleName);
        }
        string? selected = changedGame ? null : (files.SelectedItem as FileChoice)?.Path;
        string[] found = await service.FindSavesAsync(game);
        if (selected != null && File.Exists(selected) && !found.Contains(selected)) found = [.. found, selected];
        loading = true;
        try
        {
            files.Items.Clear();
            files.Items.AddRange(found.Select(path => new FileChoice(path)).ToArray());
            files.SelectedItem = files.Items.Cast<FileChoice>().FirstOrDefault(choice => choice.Path == selected)
                ?? files.Items.Cast<FileChoice>().FirstOrDefault();
        }
        finally { loading = false; }
        await ReadSelectedAsync();
    }

    private async Task ReadSelectedAsync(string? selectedSlot = null)
    {
        current = null; slots.Items.Clear();
        if (game == null || files.SelectedItem is not FileChoice file) { UpdateState(); return; }
        string path = file.Path;
        status.Text = service.Text("working");
        current = await service.InspectAsync(game, path);
        foreach (var slot in current.Slots)
            slots.Items.Add(new SlotChoice(slot, service.SlotLabel(slot.Name) + " · " + slot.Date));
        slots.SelectedItem = slots.Items.Cast<SlotChoice>().FirstOrDefault(choice => choice.Slot.Name == selectedSlot)
            ?? slots.Items.Cast<SlotChoice>().FirstOrDefault();
        tips.SetToolTip(files, path); UpdateState();
    }

    private void UpdateState()
    {
        var slot = (slots.SelectedItem as SlotChoice)?.Slot;
        var quest = slot?.Quests.SingleOrDefault(quest => quest.Id == "guiTu");
        repair.Enabled = !busy && !running && quest?.Eligible == true;
        files.Enabled = slots.Enabled = browse.Enabled = refresh.Enabled = !busy && initialized;
        if (!initialized) return;
        repair.Text = service.Text("repairNightmare");
        repair.AccessibleName = repair.Text;
        if (busy) return;
        status.Text = running ? service.Text("gameRunning") : quest != null ? service.Text(quest.Status) : service.Text("saveMissing");
    }

    private async Task BrowseAsync()
    {
        using var dialog = new OpenFileDialog { Title = service.Text("browse"), Filter = service.Text("saveFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        await run(async () =>
        {
            loading = true;
            try
            {
                var file = files.Items.Cast<FileChoice>().FirstOrDefault(choice => choice.Path == dialog.FileName);
                if (file == null) { file = new FileChoice(dialog.FileName); files.Items.Add(file); }
                files.SelectedItem = file;
            }
            finally { loading = false; }
            await ReadSelectedAsync();
        });
    }

    private async Task RepairAsync()
    {
        if (game == null || current == null || slots.SelectedItem is not SlotChoice choice || running) return;
        var result = await service.RepairAsync(game, current, choice.Slot.Name);
        await ReadSelectedAsync(result.Slot);
        string key = result.Slot.StartsWith("NightmareRun", StringComparison.Ordinal) ? "guiTuNightmareDone" : "guiTuDone";
        MessageBox.Show(FindForm(), service.Text(key, service.SlotLabel(result.Slot)), service.Text("guiTu"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        tips.SetToolTip(status, result.Backup);
    }
}
