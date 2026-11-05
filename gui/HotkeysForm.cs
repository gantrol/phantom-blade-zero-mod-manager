using System.Drawing.Drawing2D;
using System.Text.Json;

namespace PhantomBladeModManager;

internal sealed class HotkeysForm : InkForm
{
    private readonly ManagerClient client;
    private readonly ControlsSnapshot settings;
    private readonly InkGrid grid = new();
    private readonly InkLabel message = new() { Dock = DockStyle.Fill, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToggleSwitch buy = new() { Dock = DockStyle.Fill };
    private readonly ToggleSwitch upgrade = new() { Dock = DockStyle.Fill };
    private readonly TextActionButton cancel = new() { DialogResult = DialogResult.Cancel };
    private readonly TextActionButton save = new() { Accent = true };
    private readonly TextActionButton reset = new() { Width = 100 };
    private int hoverRow = -1;
    private bool saving;

    public HotkeysForm(ManagerClient client, string json, bool running)
    {
        this.client = client;
        settings = JsonSerializer.Deserialize<ControlsSnapshot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Text = client.Text("controls_title");
        Font = GameTheme.BaseFont(10f);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 680);
        MinimumSize = new Size(480, 520);
        MaximizeBox = MinimizeBox = false;
        DoubleBuffered = true;

        var layout = new InkLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 16),
            ColumnCount = 1, RowCount = 4,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        var gridFrame = new BorderedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        ConfigureGrid();
        grid.ReadOnly = running;
        gridFrame.Controls.Add(grid);
        layout.Controls.Add(gridFrame, 0, 0);

        var switches = new InkLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Padding = new Padding(0, 10, 0, 0), Margin = Padding.Empty,
        };
        switches.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        switches.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        buy.Text = client.Text("controls_tapBuy");
        buy.Checked = settings.Values.GetValueOrDefault("tap.buy") == "true";
        upgrade.Text = client.Text("controls_tapUpgrade");
        upgrade.Checked = settings.Values.GetValueOrDefault("tap.upgrade") == "true";
        buy.Enabled = upgrade.Enabled = !running;
        switches.Controls.Add(buy, 0, 0);
        switches.Controls.Add(upgrade, 0, 1);
        layout.Controls.Add(switches, 0, 1);
        switches.Visible = settings.Rows.Any(binding => binding.Mod == "quickActions");
        if (!switches.Visible) layout.RowStyles[1].Height = 0;
        message.Text = client.Text(running ? "map_close_game" : settings.BridgeEnabled ? "controls_ready" : "controls_bridgeMissing");
        message.Font = GameTheme.BaseFont(8.5f);
        message.ForeColor = running ? GameTheme.Amber : GameTheme.Muted;
        layout.Controls.Add(message, 0, 2);
        var footer = new InkLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        reset.Text = client.Text("gui_defaults");
        reset.Margin = Padding.Empty;
        reset.Enabled = !running;
        reset.Click += (_, _) =>
        {
            grid.EndEdit();
            foreach (DataGridViewRow row in grid.Rows) row.Cells[3].Value = ((ControlBinding)row.Tag!).DefaultKey;
            buy.Checked = upgrade.Checked = true;
            UpdateConflicts();
        };
        cancel.Text = cancel.AccessibleName = client.Text("map_cancel");
        save.Text = save.AccessibleName = client.Text("map_save");
        cancel.Margin = new Padding(0, 0, 10, 0);
        save.Margin = Padding.Empty;
        save.Enabled = !running;
        FormClosing += (_, e) => e.Cancel = saving;
        save.Click += async (_, _) => await SaveAsync();
        footer.Controls.Add(reset, 0, 0);
        footer.Controls.Add(cancel, 2, 0);
        footer.Controls.Add(save, 3, 0);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);
        CancelButton = cancel;
        GameTheme.Apply(this);
        CompleteLayout();
        UpdateConflicts();
    }

    private void ConfigureGrid()
    {
        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.AutoGenerateColumns = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.EditMode = DataGridViewEditMode.EditOnEnter;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 38;
        grid.LogicalRowHeight = 48;
        grid.AccessibleName = client.Text("controls_title");

        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "action", HeaderText = client.Text("controls_action"), ReadOnly = true, MinimumWidth = 230, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "scope", HeaderText = client.Text("controls_scope"), ReadOnly = true, Visible = false, Width = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "modifier", HeaderText = client.Text("controls_modifier"), ReadOnly = true, Visible = false, Width = 72, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "key",
            HeaderText = client.Text("controls_key"),
            DataSource = settings.Keys,
            Width = 148,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "status", HeaderText = client.Text("gui_state"), ReadOnly = true, Visible = false, SortMode = DataGridViewColumnSortMode.NotSortable });

        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "edit", HeaderText = "", Width = 36, ReadOnly = true,
            Text = client.Text("controls_edit"), UseColumnTextForButtonValue = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        void EditBinding(int rowIndex)
        {
            if (grid.ReadOnly || rowIndex < 0) return;
            grid.CurrentCell = grid.Rows[rowIndex].Cells[3];
            grid.BeginEdit(true);
            if (grid.EditingControl is ComboBox combo) combo.DroppedDown = true;
        }
        grid.CellContentClick += (_, e) => { if (e.ColumnIndex == 5) EditBinding(e.RowIndex); };
        grid.KeyDown += (_, e) =>
        {
            if (grid.CurrentCell?.ColumnIndex == 5 && e.KeyCode is Keys.Enter or Keys.Space)
            {
                e.SuppressKeyPress = true;
                EditBinding(grid.CurrentCell.RowIndex);
            }
        };

        foreach (var binding in settings.Rows)
        {
            var index = grid.Rows.Add(client.Text(binding.Id), client.Text("scope_" + binding.Scope), binding.Modifier ?? "—", binding.Key, "");
            grid.Rows[index].Tag = binding;
            grid.Rows[index].Height = grid.RowTemplate.Height;
            if (binding.Field != null && !binding.AllowNone)
                ((DataGridViewComboBoxCell)grid.Rows[index].Cells[3]).DataSource = settings.Keys.Where(key => key != "None").ToList();
            grid.Rows[index].Cells[0].ToolTipText = client.Text(binding.Mod);
            grid.Rows[index].Cells[5].ToolTipText = client.Text("controls_edit");
        }

        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) => { if (e.ColumnIndex == 3) UpdateConflicts(); };
        grid.DataError += (_, e) => { e.ThrowException = false; message.Text = client.Text("controls_unknown_binding"); };
        grid.CellPainting += OnGridCellPainting;
        grid.CellMouseMove += (_, e) =>
        {
            if (e.RowIndex == hoverRow) return;
            int prev = hoverRow;
            hoverRow = e.RowIndex;
            if (prev >= 0 && prev < grid.Rows.Count) grid.InvalidateRow(prev);
            if (hoverRow >= 0 && hoverRow < grid.Rows.Count) grid.InvalidateRow(hoverRow);
        };
        grid.CellMouseLeave += (_, _) =>
        {
            int prev = hoverRow;
            hoverRow = -1;
            if (prev >= 0 && prev < grid.Rows.Count) grid.InvalidateRow(prev);
        };
        grid.EditingControlShowing += (_, e) =>
        {
            if (e.Control is ComboBox combo && !SystemInformation.HighContrast)
            {
                combo.BackColor = GameTheme.Surface;
                combo.ForeColor = GameTheme.Paper;
                combo.FlatStyle = FlatStyle.Flat;
            }
        };
    }

    private void OnGridCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (SystemInformation.HighContrast) return;

        if (e.RowIndex < 0)
        {
            e.PaintBackground(e.CellBounds, false);
            var hdrRect = new Rectangle(e.CellBounds.X + 10, e.CellBounds.Y, e.CellBounds.Width - 20, e.CellBounds.Height);
            GameTheme.DrawText(e.Graphics!, Convert.ToString(e.FormattedValue), e.CellStyle!.Font, hdrRect, GameTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            using var hdrPen = new Pen(GameTheme.Line, 1f);
            e.Graphics!.DrawLine(hdrPen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
            e.Handled = true;
            return;
        }

        var g = e.Graphics!;
        bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
        bool hovered = e.RowIndex == hoverRow;
        Color bg = selected ? GameTheme.Selected : hovered ? Color.FromArgb(33, 36, 31) : GameTheme.Surface;

        using (var bgBrush = new SolidBrush(bg))
            g.FillRectangle(bgBrush, e.CellBounds);
        using (var linePen = new Pen(GameTheme.Line, 1f))
            g.DrawLine(linePen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);

        if (selected && e.ColumnIndex == 0)
            GameTheme.DrawSelectionAccent(g, e.CellBounds);

        if (e.ColumnIndex == 3)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int inset = GameTheme.Scale(grid, 10);
            var boxRect = Rectangle.Inflate(e.CellBounds, -inset, -inset);
            using var path = GameTheme.RoundedRect(boxRect, 4);
            using (var boxBrush = new SolidBrush(Color.FromArgb(22, 24, 21)))
                g.FillPath(boxBrush, path);
            using (var boxPen = new Pen(selected ? GameTheme.Cinnabar : GameTheme.Line, 1f))
                g.DrawPath(boxPen, path);

            var keyText = Convert.ToString(e.FormattedValue) ?? "";
            var binding = (ControlBinding)grid.Rows[e.RowIndex].Tag!;
            if (binding.Modifier != null && keyText != "None") keyText = binding.Modifier + " + " + keyText;
            var textRect = Rectangle.Inflate(boxRect, -GameTheme.Scale(grid, 4), 0);
            GameTheme.DrawText(g, keyText, e.CellStyle!.Font, textRect, keyText == "None" ? GameTheme.Muted : GameTheme.Paper,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            e.Handled = true;
            return;
        }

        if (e.ColumnIndex == 5)
        {
            GameIcons.Draw(g, GameIcon.Edit, e.CellBounds, grid.ReadOnly ? GameTheme.Dim : GameTheme.Paper, 15);
            if (grid.Focused && grid.CurrentCell?.RowIndex == e.RowIndex && grid.CurrentCell.ColumnIndex == 5)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(e.CellBounds, -4, -4));
        }
        else if (e.ColumnIndex == 0)
        {
            var row = grid.Rows[e.RowIndex];
            int inset = GameTheme.Scale(grid, 10);
            var rect = new Rectangle(e.CellBounds.X + inset, e.CellBounds.Y + GameTheme.Scale(grid, 4),
                e.CellBounds.Width - inset * 2, GameTheme.Scale(grid, 23));
            GameTheme.DrawText(g, Convert.ToString(e.FormattedValue), e.CellStyle!.Font, rect, GameTheme.Paper,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            rect.Y += rect.Height;
            rect.Height = GameTheme.Scale(grid, 17);
            var status = client.Text("controls_bindingState", ("scope", Convert.ToString(row.Cells[1].Value)!),
                ("status", Convert.ToString(row.Cells[4].Value) ?? ""));
            using var secondary = GameTheme.BaseFont(8f);
            GameTheme.DrawText(g, status, secondary, rect, row.Cells[4].Style.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        e.Handled = true;
    }

    private void UpdateConflicts()
    {
        static IEnumerable<string> KeysFor(DataGridViewRow row)
        {
            var binding = (ControlBinding)row.Tag!;
            var key = Convert.ToString(row.Cells[3].Value)!;
            return key == "None" ? [] : new[] { key }.Concat(key == binding.DefaultKey ? binding.Aliases ?? [] : []);
        }
        foreach (DataGridViewRow row in grid.Rows)
        {
            var binding = (ControlBinding)row.Tag!;
            var key = Convert.ToString(row.Cells[3].Value)!;
            var conflicts = grid.Rows.Cast<DataGridViewRow>().Where(other =>
            {
                var b = (ControlBinding)other.Tag!;
                return other != row && binding.Enabled && b.Enabled && KeysFor(row).Intersect(KeysFor(other)).Any()
                    && binding.Modifier == b.Modifier && (binding.Scope == "global" || b.Scope == "global" || binding.Scope == b.Scope);
            }).Select(other => client.Text(((ControlBinding)other.Tag!).Id)).ToArray();
            var warning = conflicts.Length > 0 || binding.Enabled && binding.Modifier == null && settings.NativeKeys.Contains(key);
            var status = !binding.Enabled ? client.Text("controls_inactive") : conflicts.Length > 0
                ? client.Text("controls_conflict", ("actions", string.Join(" / ", conflicts)))
                : key == "None" ? client.Text("disabled") : warning ? client.Text("controls_nativeKey") : client.Text("controls_clear");
            if (binding.Field != null && !binding.AllowNone && key == "None") { status = client.Text("controls_requiresKey"); warning = true; }
            if (binding.Mod == "map" && binding.Enabled && !settings.BridgeEnabled && conflicts.Length == 0)
                status = client.Text("controls_mapDefault");
            row.Cells[4].Value = status;
            row.Cells[4].ToolTipText = status;
            row.Cells[0].ToolTipText = client.Text(binding.Mod) + " · " + client.Text("scope_" + binding.Scope) + " · " + status;
            row.Cells[3].ToolTipText = string.Join(" / ", KeysFor(row));
            row.Cells[4].Style.ForeColor = SystemInformation.HighContrast
                ? SystemColors.WindowText
                : warning ? GameTheme.Cinnabar : (!binding.Enabled || key == "None") ? GameTheme.Dim : GameTheme.Muted;
        }
        grid.InvalidateColumn(0);
    }

    private async Task SaveAsync()
    {
        grid.EndEdit();
        var values = grid.Rows.Cast<DataGridViewRow>().ToDictionary(row => ((ControlBinding)row.Tag!).Id, row => Convert.ToString(row.Cells[3].Value)!);
        if (settings.Rows.Any(binding => binding.Mod == "quickActions"))
        {
            values["tap.buy"] = buy.Checked ? "true" : "false";
            values["tap.upgrade"] = upgrade.Checked ? "true" : "false";
        }
        saving = true;
        save.Enabled = reset.Enabled = cancel.Enabled = grid.Enabled = buy.Enabled = upgrade.Enabled = false;
        try
        {
            await client.RunAsync("controls", options: new Dictionary<string, string> { ["bindings"] = JsonSerializer.Serialize(values) });
            saving = false;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception error)
        {
            message.ForeColor = GameTheme.Cinnabar;
            message.Text = error.Message;
        }
        finally
        {
            saving = false;
            if (!IsDisposed) save.Enabled = reset.Enabled = cancel.Enabled = grid.Enabled = buy.Enabled = upgrade.Enabled = true;
        }
    }
}

internal sealed record ControlBinding(string Id, string Mod, string Scope, string Key, string? Modifier, string? Field, bool Enabled, bool AllowNone, string DefaultKey, List<string>? Aliases);
internal sealed record ControlsSnapshot(List<ControlBinding> Rows, Dictionary<string, string> Values, List<string> Keys, List<string> NativeKeys, bool BridgeEnabled);
