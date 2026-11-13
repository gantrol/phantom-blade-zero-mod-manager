using System.Text.Json;

namespace PhantomBladeModManager;

internal sealed class MapSettingsForm : InkForm
{
    private bool saving;

    public MapSettingsForm(ManagerClient client, string json, bool running)
    {
        Text = client.Text("map_settings");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(388, 534);
        Font = GameTheme.BaseFont(9f);

        using var document = JsonDocument.Parse(json);
        var values = document.RootElement.GetProperty("values");
        var defaults = document.RootElement.GetProperty("defaults").Clone();
        var layout = new InkLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(18, 8, 18, 16),
            ColumnCount = 1, RowCount = 13,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        for (int i = 0; i < 5; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.Controls.Add(new SettingsHeading { Text = client.Text("map_display") }, 0, 0);

        var checks = new Dictionary<string, ToggleSwitch>();
        void AddToggle(string key, string field, int row)
        {
            var toggle = new ToggleSwitch
            {
                Text = client.Text(key), Checked = values.GetProperty(field).GetBoolean(),
                Dock = DockStyle.Fill, Margin = new Padding(12, 0, 0, 0), Font = Font,
                Enabled = !running,
            };
            checks[field] = toggle;
            layout.Controls.Add(toggle, 0, row);
        }

        AddToggle("map_overlay", "visibility", 1);
        AddToggle("map_markers", "marker", 2);
        AddToggle("map_collectibles", "filterCollection", 3);
        AddToggle("map_minimap", "minimap", 4);
        AddToggle("map_list", "showList", 5);
        layout.Controls.Add(new SettingsHeading { Text = client.Text("map_appearance") }, 0, 6);
        var styleRow = new InkLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(12, 0, 0, 0),
        };
        styleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        styleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        styleRow.Controls.Add(new InkLabel
        {
            Text = client.Text("map_style"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        var style = new ThemedComboBox
        {
            Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 4),
            AccessibleName = client.Text("map_style"), Enabled = !running,
        };
        style.Items.AddRange([client.Text("map_native"), client.Text("map_survey")]);
        style.SelectedIndex = values.GetProperty("view").GetString() == "survey" ? 1 : 0;
        styleRow.Controls.Add(style, 1, 0);
        layout.Controls.Add(styleRow, 0, 7);
        var guidanceRow = new InkLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(12, 0, 0, 0),
        };
        guidanceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        guidanceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        guidanceRow.Controls.Add(new InkLabel
        {
            Text = client.Text("map_quest_guidance"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        var guidance = new ThemedComboBox
        {
            Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 4),
            AccessibleName = client.Text("map_quest_guidance"), Enabled = !running,
        };
        string[] guidanceModes = ["explore", "hints", "guide"];
        guidance.Items.AddRange(guidanceModes.Select(mode => (object)client.Text("map_quest_" + mode)).ToArray());
        guidance.SelectedIndex = Array.IndexOf(guidanceModes, values.GetProperty("questGuidance").GetString());
        guidanceRow.Controls.Add(guidance, 1, 0);
        layout.Controls.Add(guidanceRow, 0, 8);
        AddToggle("map_explored", "exploredOnly", 9);
        AddToggle("map_fog", "showFog", 10);

        var message = new InkLabel
        {
            Text = running ? client.Text("map_close_game") : "", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = running ? GameTheme.Amber : GameTheme.Muted,
            Font = GameTheme.BaseFont(8.5f), AutoEllipsis = true,
        };
        layout.Controls.Add(message, 0, 11);
        var footer = new InkLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var reset = new TextActionButton { Text = client.Text("gui_defaults"), Width = 100, Margin = Padding.Empty, Enabled = !running };
        var cancel = new TextActionButton { Text = client.Text("map_cancel"), DialogResult = DialogResult.Cancel, Margin = new Padding(0, 0, 10, 0) };
        var save = new TextActionButton { Text = client.Text("map_save"), Accent = true, Enabled = !running, Margin = Padding.Empty };
        reset.Click += (_, _) =>
        {
            foreach (var (field, toggle) in checks) toggle.Checked = defaults.GetProperty(field).GetBoolean();
            style.SelectedIndex = defaults.GetProperty("view").GetString() == "survey" ? 1 : 0;
            guidance.SelectedIndex = Array.IndexOf(guidanceModes, defaults.GetProperty("questGuidance").GetString());
        };
        save.Click += async (_, _) =>
        {
            saving = true;
            layout.Enabled = false;
            try
            {
                var options = checks.ToDictionary(pair => pair.Key, pair => pair.Value.Checked ? "true" : "false");
                options["view"] = style.SelectedIndex == 0 ? "native" : "survey";
                options["questGuidance"] = guidanceModes[guidance.SelectedIndex];
                await client.RunAsync("map-settings", "map", options);
                saving = false;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception error)
            {
                message.ForeColor = GameTheme.CinnabarHover;
                message.Text = error.Message;
                layout.Enabled = true;
            }
            finally { saving = false; }
        };
        footer.Controls.Add(reset, 0, 0);
        footer.Controls.Add(cancel, 2, 0);
        footer.Controls.Add(save, 3, 0);
        layout.Controls.Add(footer, 0, 12);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        FormClosing += (_, e) => e.Cancel = saving;
        GameTheme.Apply(this);
        CompleteLayout();
    }
}
