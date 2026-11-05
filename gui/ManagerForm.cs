using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace PhantomBladeModManager;

internal sealed class ManagerForm : InkForm
{
    private static readonly (string Key, GameIcon Glyph)[] RowActions =
    [
        ("gui_config", GameIcon.Settings),
        ("gui_update", GameIcon.Refresh),
        ("gui_restoreSave", GameIcon.Restore),
        ("gui_restore", GameIcon.Delete),
        ("gui_folder", GameIcon.Folder),
    ];

    private readonly ManagerClient client;
    private readonly string root;
    private readonly InkGrid grid = new();
    private readonly InkLabel title = new();
    private readonly InkLabel brand = new();
    private readonly InkLabel brandSubtitle = new();
    private readonly InkLabel gameDirLabel = new();
    private readonly InkPathLabel gamePath = new();
    private readonly InkLabel gameState = new();
    private readonly InkLabel feedback = new();
    private readonly InkLabel selectionDetail = new();
    private readonly TabButton featureTab = new();
    private readonly TabButton remedyTab = new();
    private readonly ToolTip toolTip = new();
    private readonly ContextMenuStrip rowMenu = new();
    private readonly System.Windows.Forms.Timer gameTimer = new() { Interval = 3000 };
    private readonly Dictionary<string, IconButton> buttons = [];
    private Snapshot? snapshot;
    private string activeCategory = "feature";
    private int hoverRow = -1;
    private int hoverCol = -1;
    private int hoverAction = -1;
    private bool busy;
    private bool initialized;
    private bool checkingGame;

    public ManagerForm(string root)
    {
        this.root = root;
        client = new ManagerClient(root);
        Text = "PhantomBladeModManager";
        Font = GameTheme.BaseFont(10f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(940, 760);
        MinimumSize = new Size(860, 640);
        LandscapeHeader = true;
        DoubleBuffered = true;

        BuildLayout();
        GameTheme.Apply(this);
        CompleteLayout();

        Shown += async (_, _) => await RefreshAsync();
        FormClosing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            feedback.Text = client.Text("gui_finishing");
        };
        FormClosed += (_, _) =>
        {
            gameTimer.Dispose();
            toolTip.Dispose();
            rowMenu.Dispose();
        };
        gameTimer.Tick += async (_, _) => await CheckGameAsync();
    }

    private Plugin? Selected => grid.CurrentRow?.Tag as Plugin;

    private void BuildLayout()
    {
        var layout = new InkLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16, 10, 16, 6),
            Margin = Padding.Empty,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        var header = new InkLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
        };
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        var branding = new InkLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
            Margin = new Padding(10, 0, 0, 0),
        };
        branding.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        branding.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        branding.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        branding.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        brand.Font = GameTheme.BrandFont(27f);
        brand.AutoSize = true;
        brand.Dock = DockStyle.Fill;
        brand.TextAlign = ContentAlignment.MiddleLeft;
        brand.Margin = Padding.Empty;
        title.Font = GameTheme.TitleFont(18f);
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.Margin = new Padding(12, 0, 0, 0);
        brandSubtitle.Font = new Font("Georgia", 6.5f);
        brandSubtitle.ForeColor = GameTheme.Muted;
        brandSubtitle.Dock = DockStyle.Fill;
        brandSubtitle.TextAlign = ContentAlignment.MiddleLeft;
        brandSubtitle.Padding = new Padding(10, 0, 0, 4);
        branding.Controls.Add(brand, 0, 0);
        branding.Controls.Add(title, 1, 0);
        branding.Controls.Add(brandSubtitle, 0, 1);
        branding.SetColumnSpan(brandSubtitle, 2);
        header.Controls.Add(branding, 0, 0);

        var toolbar = new InkLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty,
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 166));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var pathRow = new InkLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = new Padding(4, 13, 8, 11),
        };
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pathRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gameDirLabel.AutoSize = true;
        gameDirLabel.Dock = DockStyle.Fill;
        gameDirLabel.TextAlign = ContentAlignment.MiddleLeft;
        gameDirLabel.Font = GameTheme.BaseFont(8.5f);
        gameDirLabel.ForeColor = GameTheme.Muted;
        gameDirLabel.Margin = new Padding(0, 0, 8, 0);
        pathRow.Controls.Add(gameDirLabel, 0, 0);

        var pathBox = new BorderedPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(7, 5, 2, 3), Margin = Padding.Empty,
        };
        gamePath.Dock = DockStyle.Fill;
        gamePath.Font = GameTheme.BaseFont(8f);
        var browse = new IconButton(GameIcon.Folder, 28, 24, 17f)
        {
            Dock = DockStyle.Right, Borderless = true, Margin = Padding.Empty,
        };
        browse.Click += async (_, _) => await BrowseGameDirectoryAsync();
        buttons["gui_selectGameDir"] = browse;
        pathBox.Controls.Add(gamePath);
        pathBox.Controls.Add(browse);
        pathRow.Controls.Add(pathBox, 1, 0);
        toolbar.Controls.Add(pathRow, 0, 0);

        gameState.Dock = DockStyle.Fill;
        gameState.TextAlign = ContentAlignment.MiddleLeft;
        gameState.Padding = new Padding(15, 0, 0, 0);
        gameState.Margin = new Padding(0, 6, 0, 0);
        gameState.AutoEllipsis = true;
        gameState.Font = GameTheme.BaseFont(8f);
        gameState.Paint += PaintGameState;
        toolbar.Controls.Add(gameState, 1, 0);
        var globalBar = new InkFlowPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = false,
            Margin = Padding.Empty, Padding = Padding.Empty,
        };
        AddGlobalButton(globalBar, "gui_quests", GameIcon.Layers, OpenQuestsAsync);
        AddGlobalButton(globalBar, "controls_title", GameIcon.Keyboard, OpenControlsAsync);
        AddGlobalButton(globalBar, "gui_log", GameIcon.Document, () => OpenPathAsync(snapshot?.Log, true));
        AddGlobalButton(globalBar, "gui_refresh", GameIcon.Refresh, RefreshAsync);
        toolbar.Controls.Add(globalBar, 2, 0);
        header.Controls.Add(toolbar, 0, 1);
        layout.Controls.Add(header, 0, 0);

        var tabBar = new InkFlowPanel
        {
            Dock = DockStyle.Fill, WrapContents = false,
            Margin = new Padding(4, 0, 4, 12), Padding = new Padding(0, 3, 0, 0),
        };
        tabBar.Paint += (_, e) =>
        {
            using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : GameTheme.Line);
            e.Graphics.DrawLine(pen, 0, tabBar.Height - 1, tabBar.Width, tabBar.Height - 1);
        };
        featureTab.Active = true;
        featureTab.Glyph = GameIcon.Swords;
        remedyTab.Glyph = GameIcon.Document;
        featureTab.Click += (_, _) => SwitchCategory("feature");
        remedyTab.Click += (_, _) => SwitchCategory("remedy");
        tabBar.Controls.Add(featureTab);
        tabBar.Controls.Add(remedyTab);
        layout.Controls.Add(tabBar, 0, 1);

        var gridFrame = new BorderedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        ConfigureGrid();
        gridFrame.Controls.Add(grid);
        layout.Controls.Add(gridFrame, 0, 2);

        var footer = new InkLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(4, 3, 4, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 53));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47));
        feedback.Dock = DockStyle.Fill;
        feedback.TextAlign = ContentAlignment.MiddleLeft;
        feedback.AutoEllipsis = true;
        feedback.Font = GameTheme.BaseFont(8f);
        feedback.ForeColor = GameTheme.Muted;
        footer.Controls.Add(feedback, 0, 0);
        selectionDetail.Dock = DockStyle.Fill;
        selectionDetail.TextAlign = ContentAlignment.MiddleRight;
        selectionDetail.AutoEllipsis = true;
        selectionDetail.Font = GameTheme.BaseFont(8f);
        selectionDetail.ForeColor = GameTheme.Muted;
        footer.Controls.Add(selectionDetail, 1, 0);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);
        UpdateControls();
    }

    private void ConfigureGrid()
    {
        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.MultiSelect = false;
        grid.RowHeadersVisible = false;
        grid.AutoGenerateColumns = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 40;
        grid.LogicalRowHeight = 54;
        grid.ReadOnly = true;
        grid.ShowCellToolTips = false;

        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "active",
            Width = 92,
            Resizable = DataGridViewTriState.False,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "name",
            ReadOnly = true,
            MinimumWidth = 235,
            FillWeight = 58,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "gui_version",
            ReadOnly = true,
            MinimumWidth = 180,
            FillWeight = 30,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "gui_actions",
            ReadOnly = true,
            Width = 230,
            Resizable = DataGridViewTriState.False,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        grid.SelectionChanged += (_, _) => UpdateControls();
        grid.CellPainting += OnGridCellPainting;
        grid.CellMouseMove += OnGridCellMouseMove;
        grid.CellMouseLeave += OnGridCellMouseLeave;
        grid.CellMouseClick += OnGridCellMouseClick;
        grid.KeyDown += OnGridKeyDown;
        foreach (var (key, _) in RowActions)
        {
            var item = rowMenu.Items.Add(key);
            item.Tag = key;
            item.Click += async (_, _) =>
            {
                if (Selected is { } plugin && IsRowActionEnabled(plugin, key))
                    await ExecuteRowActionAsync(plugin, key);
            };
        }
        rowMenu.Opening += (_, e) =>
        {
            e.Cancel = Selected == null;
            foreach (ToolStripItem item in rowMenu.Items)
                item.Enabled = Selected is { } plugin && IsRowActionEnabled(plugin, (string)item.Tag!);
        };
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0) grid.CurrentCell = grid.Rows[e.RowIndex].Cells[1];
        };
        grid.ContextMenuStrip = rowMenu;
    }

    private void AddGlobalButton(FlowLayoutPanel panel, string key, GameIcon glyph, Func<Task> action)
    {
        var btn = new IconButton(glyph, 70, 52, 21f) { Borderless = true, Margin = new Padding(2, 0, 0, 0) };
        btn.Click += async (_, _) => await action();
        buttons[key] = btn;
        panel.Controls.Add(btn);
    }

    private void SwitchCategory(string category)
    {
        if (activeCategory == category) return;
        activeCategory = category;
        featureTab.Active = category == "feature";
        remedyTab.Active = category == "remedy";
        PopulateGrid(null);
    }

    private void Localize()
    {
        Text = client.Text("title");
        brand.Text = client.Text("gui_brand");
        title.Text = client.Text("gui_managerTitle");
        brandSubtitle.Text = client.Text("gui_brandSubtitle");
        gameDirLabel.Text = client.Text("gui_gameDirectory");
        grid.AccessibleName = client.Text("gui_plugins");
        gamePath.AccessibleName = client.Text("gui_gameDirectory");
        foreach (DataGridViewColumn column in grid.Columns)
            column.HeaderText = client.Text(column.Name == "active" ? "on" : column.Name == "name" ? "gui_modName" : column.Name);
        foreach (var (key, button) in buttons)
        {
            var label = client.Text(key);
            button.AccessibleName = label;
            if (button.Borderless && key != "gui_selectGameDir")
                button.Caption = client.Text(key + "_short");
            toolTip.SetToolTip(button, label);
        }
        foreach (ToolStripItem item in rowMenu.Items) item.Text = client.Text((string)item.Tag!);
        UpdateTabLabels();
    }

    private void UpdateTabLabels()
    {
        int featureCount = snapshot?.Plugins.Count(p => p.Category == "feature" && (!p.Retired || p.LoadState != "absent")) ?? 0;
        int remedyCount = snapshot?.Plugins.Count(p => p.Category == "remedy" && (!p.Retired || p.LoadState != "absent")) ?? 0;
        featureTab.Text = snapshot != null ? $"{client.Text("category_feature")} ({featureCount})" : client.Text("category_feature");
        remedyTab.Text = snapshot != null ? $"{client.Text("category_remedy")} ({remedyCount})" : client.Text("category_remedy");
        featureTab.AccessibleName = featureTab.Text;
        remedyTab.AccessibleName = remedyTab.Text;
        remedyTab.Visible = snapshot == null || remedyCount > 0;
    }

    private string QuestManagerPath =>
        Path.GetFullPath(Path.Combine(root, "mods/remedies/phantom-blade-chaos-reset/dist/PhantomBladeQuestManager-current/PhantomBladeQuestManager.exe"));

    private async Task RefreshAsync()
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            if (!initialized)
            {
                await client.InitializeAsync();
                Localize();
                initialized = true;
            }
            feedback.Text = client.Text("gui_loading");
            await LoadSnapshotAsync();
            ShowSummary();
            gameTimer.Start();
        }
        catch (Exception error) when (error.InnerException is ManagerException { Code: "game_not_found" })
        {
            snapshot = null;
            grid.Rows.Clear();
            gamePath.Text = client.Text("game_not_found");
            gameState.Text = "";
            gameState.Invalidate();
            feedback.Text = client.Text("game_not_found");
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async Task BrowseGameDirectoryAsync()
    {
        if (busy || snapshot is { GameRunning: true }) return;
        using var dialog = new FolderBrowserDialog
        {
            Description = client.Text("gui_selectGameDir"),
            UseDescriptionForTitle = true,
            SelectedPath = snapshot?.Game ?? "",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            return;
        SetBusy(true);
        try
        {
            await client.SetGameDirectoryAsync(dialog.SelectedPath);
        }
        catch (Exception error)
        {
            SetBusy(false);
            ShowError(error);
            return;
        }
        SetBusy(false);
        await RefreshAsync();
    }

    private async Task LoadSnapshotAsync()
    {
        var selectedId = Selected?.Id;
        snapshot = await client.StatusAsync();
        UpdateTabLabels();
        PopulateGrid(selectedId);
        gamePath.Text = snapshot.Game;
        toolTip.SetToolTip(gamePath, snapshot.Game);
        UpdateControls();
    }

    private void PopulateGrid(string? selectedId)
    {
        if (snapshot == null) return;
        hoverRow = -1;
        hoverCol = -1;
        hoverAction = -1;
        grid.Rows.Clear();
        var filtered = snapshot.Plugins.Where(p =>
            p.Category == activeCategory && (!p.Retired || p.LoadState != "absent"));

        foreach (var plugin in filtered)
        {
            var versionSummary = FormatVersionText(plugin) + " · " + FormatStatusText(plugin);
            var rowIndex = grid.Rows.Add(
                plugin.LoadState == "enabled",
                client.Text(plugin.Id) + " — " + client.Text("mod_description_" + plugin.Id),
                versionSummary,
                "");
            var row = grid.Rows[rowIndex];
            row.Height = grid.RowTemplate.Height;
            row.Tag = plugin;
        }

        var selected = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => (row.Tag as Plugin)?.Id == selectedId)
            ?? grid.Rows.Cast<DataGridViewRow>().FirstOrDefault();
        if (selected != null)
        {
            selected.Selected = true;
            grid.CurrentCell = selected.Cells[1];
        }
        UpdateControls();
    }

    private string FormatVersionText(Plugin plugin)
    {
        if (plugin.LoadState == "absent")
            return plugin.SourceVersion != null ? $"— ({plugin.SourceVersion})" : "—";
        if (plugin.SourceState == "different" && plugin.InstalledVersion != null && plugin.SourceVersion != null && plugin.InstalledVersion != plugin.SourceVersion)
            return $"{plugin.InstalledVersion} → {plugin.SourceVersion}";
        return plugin.InstalledVersion ?? plugin.SourceVersion ?? "—";
    }

    private string FormatStatusText(Plugin plugin)
    {
        if (plugin.Retired) return client.Text("gui_retired");
        if (plugin.IncompatibleBuild) return client.Text("unsupported_build");
        if (plugin.SourceState == "modified") return client.Text("gui_modified");
        if (plugin.LoadState == "conflict") return client.Text("conflict");
        if (plugin.LoadState == "absent") return client.Text("absent");
        if (plugin.SourceState == "different") return client.Text("gui_updateAvailable");
        if (plugin.SourceState == "unknown") return client.Text("gui_sourceUnavailable");
        if (plugin.SourceState == "same") return client.Text("gui_latest");
        return client.Text(plugin.LoadState);
    }

    private Color StatusDotColor(Plugin plugin)
    {
        if (plugin.Retired) return GameTheme.Dim;
        if (plugin.IncompatibleBuild || plugin.LoadState == "conflict") return GameTheme.Cinnabar;
        if (plugin.LoadState == "absent") return GameTheme.Dim;
        if (plugin.SourceState == "different") return GameTheme.Amber;
        return plugin.SourceState == "same" ? GameTheme.Jade : GameTheme.Muted;
    }

    private void OnGridCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0)
        {
            if (SystemInformation.HighContrast) return;
            e.PaintBackground(e.CellBounds, false);
            var hdrFlags = e.ColumnIndex == 0
                ? TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                : TextFormatFlags.Left | TextFormatFlags.VerticalCenter;
            var hdrRect = e.ColumnIndex == 0
                ? e.CellBounds
                : new Rectangle(e.CellBounds.X + 10, e.CellBounds.Y, e.CellBounds.Width - 20, e.CellBounds.Height);
            GameTheme.DrawText(e.Graphics!, Convert.ToString(e.FormattedValue), e.CellStyle!.Font, hdrRect, GameTheme.Paper, hdrFlags);
            using var hdrLine = new Pen(GameTheme.Line, 1f);
            e.Graphics!.DrawLine(hdrLine, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
            e.Handled = true;
            return;
        }

        if (grid.Rows[e.RowIndex].Tag is not Plugin plugin) return;
        var g = e.Graphics!;
        bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
        bool rowHovered = e.RowIndex == hoverRow;

        Color bg = SystemInformation.HighContrast
            ? (selected ? SystemColors.Highlight : SystemColors.Window)
            : (selected ? GameTheme.Selected : rowHovered ? Color.FromArgb(33, 36, 31) : GameTheme.Surface);
        using (var bgBrush = new SolidBrush(bg))
            g.FillRectangle(bgBrush, e.CellBounds);

        using (var linePen = new Pen(GameTheme.Line, 1f))
            g.DrawLine(linePen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);

        if (selected && e.ColumnIndex == 0)
            GameTheme.DrawSelectionAccent(g, e.CellBounds);

        if (e.ColumnIndex == 0)
        {
            bool enabled = plugin.LoadState == "enabled";
            bool canToggle = !busy && snapshot?.GameRunning == false && CanToggle(plugin);
            bool pillHover = e.RowIndex == hoverRow && hoverCol == 0 && canToggle;
            GameTheme.DrawTogglePill(g, e.CellBounds, enabled, canToggle, pillHover);
        }
        else if (e.ColumnIndex == 1)
        {
            var name = client.Text(plugin.Id);
            Color fg = SystemInformation.HighContrast
                ? (selected ? SystemColors.HighlightText : SystemColors.WindowText)
                : (plugin.LoadState == "enabled" ? GameTheme.Paper : Color.FromArgb(208, 202, 188));
            using var nameFont = GameTheme.TitleFont(10.5f);
            using var descriptionFont = GameTheme.BaseFont(8.5f);
            int inset = GameTheme.Scale(grid, 10);
            int top = e.CellBounds.Y + GameTheme.Scale(grid, 5);
            int nameHeight = GameTheme.Scale(grid, 24);
            var textRect = new Rectangle(e.CellBounds.X + inset, top, e.CellBounds.Width - inset * 2, nameHeight);
            GameTheme.DrawText(g, name, nameFont, textRect, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            textRect.Y += nameHeight;
            textRect.Height = GameTheme.Scale(grid, 18);
            GameTheme.DrawText(g, client.Text("mod_description_" + plugin.Id), descriptionFont, textRect,
                SystemInformation.HighContrast ? fg : GameTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        else if (e.ColumnIndex == 2)
        {
            var verText = FormatVersionText(plugin);
            var statusText = FormatStatusText(plugin);
            var dotColor = StatusDotColor(plugin);

            int x = e.CellBounds.X + GameTheme.Scale(grid, 10);
            int cy = e.CellBounds.Y + e.CellBounds.Height / 2;

            Color verColor = SystemInformation.HighContrast
                ? (selected ? SystemColors.HighlightText : SystemColors.WindowText)
                : (plugin.LoadState == "absent" ? GameTheme.Muted : GameTheme.Paper);

            var verSize = TextRenderer.MeasureText(g, verText, e.CellStyle!.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int versionWidth = Math.Min(verSize.Width + GameTheme.Scale(grid, 4), Math.Max(0, e.CellBounds.Width - GameTheme.Scale(grid, 90)));
            var verRect = new Rectangle(x, e.CellBounds.Y, versionWidth, e.CellBounds.Height);
            GameTheme.DrawText(g, verText, e.CellStyle.Font, verRect, verColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            int dotX = verRect.Right + GameTheme.Scale(grid, 8);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int dotSize = GameTheme.Scale(grid, 10);
            using (var dotBrush = new SolidBrush(SystemInformation.HighContrast ? verColor : dotColor))
                g.FillEllipse(dotBrush, dotX, cy - dotSize / 2, dotSize, dotSize);
            if (plugin.SourceState == "same")
            {
                using var check = new Pen(GameTheme.Ink, Math.Max(1, g.DpiX / 96f));
                g.DrawLines(check, [new Point(dotX + dotSize / 4, cy), new Point(dotX + dotSize * 2 / 5, cy + dotSize / 5),
                    new Point(dotX + dotSize * 3 / 4, cy - dotSize / 4)]);
            }

            using var statusFont = GameTheme.BaseFont(9f);
            Color statusColor = plugin.SourceState == "different" && plugin.LoadState != "absent"
                ? GameTheme.Amber
                : (plugin.IncompatibleBuild || plugin.LoadState == "conflict")
                    ? GameTheme.Cinnabar
                    : plugin.SourceState == "same" ? GameTheme.Jade : GameTheme.Muted;
            if (SystemInformation.HighContrast) statusColor = verColor;
            var statusRect = new Rectangle(dotX + dotSize + GameTheme.Scale(grid, 5), e.CellBounds.Y, Math.Max(0, e.CellBounds.Right - dotX - dotSize - GameTheme.Scale(grid, 9)), e.CellBounds.Height);
            GameTheme.DrawText(g, statusText, statusFont, statusRect, statusColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
        else if (e.ColumnIndex == 3)
        {
            for (int i = 0; i < RowActions.Length; i++)
            {
                var btnRect = GetActionSlotBounds(e.CellBounds, i);
                bool enabled = IsRowActionEnabled(plugin, RowActions[i].Key);
                bool hovered = e.RowIndex == hoverRow && hoverCol == 3 && hoverAction == i && enabled;

                if (hovered && !SystemInformation.HighContrast)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using var path = GameTheme.RoundedRect(btnRect, 4);
                    using var hBrush = new SolidBrush(GameTheme.Hover);
                    g.FillPath(hBrush, path);
                    using var hPen = new Pen(GameTheme.Muted, 1f);
                    g.DrawPath(hPen, path);
                }

                bool highlightUpdate = RowActions[i].Key == "gui_update" && enabled && plugin.InstalledVersion != null;
                Color iconColor = SystemInformation.HighContrast
                    ? (enabled ? selected ? SystemColors.HighlightText : SystemColors.WindowText : SystemColors.GrayText)
                    : (!enabled ? GameTheme.Dim : highlightUpdate ? GameTheme.Amber : hovered ? GameTheme.Paper : Color.FromArgb(195, 189, 175));

                GameIcons.Draw(g, RowActions[i].Glyph, btnRect, iconColor, 20);
            }
        }

        if (grid.Focused && (e.State & DataGridViewElementStates.Displayed) != 0 &&
            grid.CurrentCell?.RowIndex == e.RowIndex && grid.CurrentCell.ColumnIndex == e.ColumnIndex)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(e.CellBounds, -4, -4),
                SystemInformation.HighContrast ? SystemColors.HighlightText : GameTheme.Muted, bg);
        e.Handled = true;
    }

    private Rectangle GetActionSlotBounds(Rectangle cellBounds, int index)
    {
        int btnW = cellBounds.Width / RowActions.Length;
        int btnH = GameTheme.Scale(grid, 32);
        int startX = cellBounds.X + index * btnW;
        int startY = cellBounds.Y + (cellBounds.Height - btnH) / 2;
        return new Rectangle(startX, startY, btnW, btnH);
    }

    private int HitTestActionSlot(int columnIndex, int rowIndex, Point cellLocation)
    {
        if (columnIndex != 3 || rowIndex < 0) return -1;
        var cellRect = new Rectangle(0, 0, grid.Columns[3].Width, grid.Rows[rowIndex].Height);
        for (int i = 0; i < RowActions.Length; i++)
        {
            if (GetActionSlotBounds(cellRect, i).Contains(cellLocation))
                return i;
        }
        return -1;
    }

    private void OnGridCellMouseMove(object? sender, DataGridViewCellMouseEventArgs e)
    {
        int newRow = e.RowIndex;
        int newCol = e.ColumnIndex;
        int newAction = HitTestActionSlot(e.ColumnIndex, e.RowIndex, e.Location);

        if (newRow == hoverRow && newCol == hoverCol && newAction == hoverAction)
            return;

        int prevRow = hoverRow;
        hoverRow = newRow;
        hoverCol = newCol;
        hoverAction = newAction;

        if (prevRow >= 0 && prevRow < grid.Rows.Count)
            grid.InvalidateRow(prevRow);
        if (hoverRow >= 0 && hoverRow < grid.Rows.Count && hoverRow != prevRow)
            grid.InvalidateRow(hoverRow);

        if (hoverRow >= 0 && hoverRow < grid.Rows.Count && grid.Rows[hoverRow].Tag is Plugin plugin)
        {
            if (hoverCol == 0 && !busy && snapshot?.GameRunning == false && CanToggle(plugin))
            {
                grid.Cursor = Cursors.Hand;
                toolTip.SetToolTip(grid, client.Text(plugin.LoadState == "enabled" ? "off" : "on"));
                return;
            }
            if (hoverCol == 3 && hoverAction >= 0)
            {
                var key = RowActions[hoverAction].Key;
                bool enabled = IsRowActionEnabled(plugin, key);
                grid.Cursor = enabled ? Cursors.Hand : Cursors.Default;
                toolTip.SetToolTip(grid, client.Text(key));
                return;
            }
        }

        grid.Cursor = Cursors.Default;
        toolTip.SetToolTip(grid, null);
    }

    private void OnGridCellMouseLeave(object? sender, DataGridViewCellEventArgs e)
    {
        int prevRow = hoverRow;
        hoverRow = -1;
        hoverCol = -1;
        hoverAction = -1;
        grid.Cursor = Cursors.Default;
        toolTip.SetToolTip(grid, null);
        if (prevRow >= 0 && prevRow < grid.Rows.Count)
            grid.InvalidateRow(prevRow);
    }

    private async void OnGridCellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
        if (grid.Rows[e.RowIndex].Tag is not Plugin plugin) return;

        if (e.ColumnIndex == 0)
        {
            if (busy || snapshot?.GameRunning != false || !CanToggle(plugin)) return;
            bool enabled = plugin.LoadState == "enabled";
            await ChangeAsync(plugin, enabled ? "off" : "on");
        }
        else if (e.ColumnIndex == 3)
        {
            int idx = HitTestActionSlot(e.ColumnIndex, e.RowIndex, e.Location);
            if (idx < 0) return;
            var key = RowActions[idx].Key;
            if (!IsRowActionEnabled(plugin, key)) return;
            await ExecuteRowActionAsync(plugin, key);
        }
    }

    private async void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (Selected is not { } plugin) return;
        if (e.KeyCode == Keys.Space && !busy && snapshot?.GameRunning == false && CanToggle(plugin))
        {
            e.Handled = true;
            await ChangeAsync(plugin, plugin.LoadState == "enabled" ? "off" : "on");
        }
        else if (e.KeyCode == Keys.Enter && !busy)
        {
            e.Handled = true;
            if (IsRowActionEnabled(plugin, "gui_config"))
                await ExecuteRowActionAsync(plugin, "gui_config");
            else if (IsRowActionEnabled(plugin, "gui_update"))
                await ExecuteRowActionAsync(plugin, "gui_update");
        }
    }

    private Task ExecuteRowActionAsync(Plugin plugin, string key) => key switch
    {
        "gui_config" => OpenPluginSettingsAsync(plugin),
        "gui_update" => ChangeAsync(plugin, "install"),
        "gui_restoreSave" => ChangeAsync(plugin, "restore-save"),
        "gui_restore" => ChangeAsync(plugin, "restore"),
        "gui_folder" => OpenPathAsync(plugin.Directory),
        _ => Task.CompletedTask,
    };

    private bool IsRowActionEnabled(Plugin plugin, string key)
    {
        if (busy) return false;
        bool writable = snapshot is { GameRunning: false } && plugin.LoadState != "conflict";
        return key switch
        {
            "gui_config" => plugin.Config != null,
            "gui_update" => writable && !plugin.IncompatibleBuild && plugin.SourceVersion != null && plugin.SourceState != "same",
            "gui_restoreSave" => snapshot is { GameRunning: false } && plugin.BackupCount > 0,
            "gui_restore" => writable && plugin.LoadState is "enabled" or "disabled" && !plugin.Retired,
            "gui_folder" => plugin.Directory != null && Directory.Exists(plugin.Directory),
            _ => false,
        };
    }

    private void PaintGameState(object? sender, PaintEventArgs e)
    {
        if (snapshot == null || string.IsNullOrEmpty(gameState.Text) || SystemInformation.HighContrast) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color dotColor = snapshot.GameRunning ? GameTheme.Amber : GameTheme.Jade;
        int dotX = GameTheme.Scale(gameState, 2);
        int dotY = (gameState.Height - GameTheme.Scale(gameState, 8)) / 2;
        using var brush = new SolidBrush(dotColor);
        g.FillEllipse(brush, dotX, dotY, GameTheme.Scale(gameState, 8), GameTheme.Scale(gameState, 8));
    }

    private async Task ChangeAsync(Plugin plugin, string action)
    {
        if (busy || snapshot == null || snapshot.GameRunning) return;
        SetBusy(true);
        feedback.Text = client.Text("gui_applying", ("name", client.Text(plugin.Id)));
        Exception? failure = null;
        try { await client.RunAsync(action, plugin.Id); }
        catch (Exception error) { failure = error; }
        try { await LoadSnapshotAsync(); }
        catch (Exception error) { failure ??= error; snapshot = null; }
        SetBusy(false);
        if (failure != null) ShowError(failure);
        else feedback.Text = client.Text("done", ("action", client.Text(action)), ("id", client.Text(plugin.Id)));
    }

    private void SetBusy(bool value)
    {
        busy = value;
        UseWaitCursor = value;
        UpdateControls();
    }

    private static bool CanToggle(Plugin plugin) =>
        !plugin.Retired &&
        (plugin.IncompatibleBuild
            ? plugin.LoadState == "enabled"
            : (plugin.LoadState is "enabled" or "disabled" || (plugin.LoadState == "absent" && plugin.SourceVersion != null)));

    private void UpdateControls()
    {
        if (buttons.Count == 0) return;
        var plugin = Selected;
        var hasQuestManager = File.Exists(QuestManagerPath);
        buttons["gui_refresh"].Enabled = !busy;
        buttons["gui_selectGameDir"].Enabled = !busy && (snapshot == null || !snapshot.GameRunning);
        buttons["gui_log"].Enabled = !busy && snapshot != null;
        buttons["controls_title"].Enabled = !busy && initialized && snapshot != null;
        buttons["gui_quests"].Visible = hasQuestManager;
        buttons["gui_quests"].Enabled = !busy && hasQuestManager;
        grid.Enabled = !busy;

        if (snapshot != null)
        {
            var stateText = client.Text(snapshot.GameRunning ? "gui_running" : "gui_stopped");
            gameState.Text = string.IsNullOrEmpty(snapshot.GameBuildId) ? stateText : client.Text("gui_buildState", ("build", snapshot.GameBuildId), ("state", stateText));
            toolTip.SetToolTip(gameState, gameState.Text);
            gameState.ForeColor = snapshot.GameRunning ? GameTheme.Amber : GameTheme.Muted;
            gameState.AccessibleDescription = client.Text(snapshot.GameRunning ? "game_running" : "stopped");
            gameState.Invalidate();
        }

        var enabled = snapshot?.Plugins.Where(p => p.LoadState == "enabled").ToArray() ?? [];
        bool current = enabled.Length > 0 && enabled.All(p => p.SourceState == "same" && !p.IncompatibleBuild);
        selectionDetail.Text = current ? client.Text("gui_allCurrent")
            : enabled.Length > 0 ? client.Text("gui_updatesPending") : "";
        selectionDetail.ForeColor = current ? GameTheme.Jade : GameTheme.Muted;
        toolTip.SetToolTip(selectionDetail, plugin?.LatestBackup is { } backup
            ? client.Text("gui_lastBackup", ("date", backup.ToLocalTime().ToString("g", client.Culture))) : null);
        grid.Invalidate();
    }

    private void ShowSummary()
    {
        var visible = snapshot?.Plugins.Where(p => !p.Retired || p.LoadState != "absent").ToArray() ?? [];
        feedback.Text = client.Text("gui_modCount", ("total", visible.Length.ToString(client.Culture)),
            ("features", visible.Count(p => p.Category == "feature").ToString(client.Culture)),
            ("remedies", visible.Count(p => p.Category == "remedy").ToString(client.Culture)));
    }

    private async Task CheckGameAsync()
    {
        if (busy || checkingGame || snapshot == null || WindowState == FormWindowState.Minimized) return;
        checkingGame = true;
        try
        {
            var running = await ManagerClient.GameRunningAsync();
            if (IsDisposed || busy || snapshot == null) return;
            if (snapshot.GameRunning != running)
            {
                snapshot = snapshot with { GameRunning = running };
                UpdateControls();
            }
        }
        catch (InvalidOperationException) { }
        finally { checkingGame = false; }
    }

    private async Task OpenPluginSettingsAsync(Plugin plugin)
    {
        if (plugin.Id is "quickActions" or "sprint" or "teleport") { await OpenControlsAsync(); return; }
        if (plugin.Id != "map") { await OpenPathAsync(plugin.Config, true); return; }
        if (busy) return;
        SetBusy(true);
        try
        {
            var data = await client.RunAsync("map-settings", "map");
            using var dialog = new MapSettingsForm(client, data, snapshot?.GameRunning != false);
            dialog.ShowDialog(this);
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async Task OpenControlsAsync()
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            var data = await client.RunAsync("controls");
            using var dialog = new HotkeysForm(client, data, snapshot?.GameRunning != false);
            if (dialog.ShowDialog(this) == DialogResult.OK) feedback.Text = client.Text("controls_saved");
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private Task OpenQuestsAsync()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(QuestManagerPath) { UseShellExecute = true });
        }
        catch (Exception error) { ShowError(error); }
        return Task.CompletedTask;
    }

    private Task OpenPathAsync(string? path, bool text = false)
    {
        if (path == null) return Task.CompletedTask;
        try
        {
            var start = new ProcessStartInfo(text ? "notepad.exe" : "explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
        }
        catch (Exception error) { ShowError(error); }
        return Task.CompletedTask;
    }

    private void ShowError(Exception error)
    {
        feedback.Text = client.Text("gui_failed");
        MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
