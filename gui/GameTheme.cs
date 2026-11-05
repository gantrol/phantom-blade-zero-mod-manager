using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PhantomBladeModManager;

internal static class GameTheme
{
    public static readonly Color Ink = Color.FromArgb(16, 20, 17);
    public static readonly Color Surface = Color.FromArgb(23, 27, 23);
    public static readonly Color Hover = Color.FromArgb(38, 42, 34);
    public static readonly Color Selected = Color.FromArgb(46, 50, 39);
    public static readonly Color Paper = Color.FromArgb(224, 214, 190);
    public static readonly Color Muted = Color.FromArgb(166, 159, 138);
    public static readonly Color Dim = Color.FromArgb(86, 89, 76);
    public static readonly Color Cinnabar = Color.FromArgb(150, 76, 55);
    public static readonly Color CinnabarHover = Color.FromArgb(182, 98, 73);
    public static readonly Color Jade = Color.FromArgb(126, 161, 102);
    public static readonly Color Amber = Color.FromArgb(212, 164, 86);
    public static readonly Color Line = Color.FromArgb(59, 62, 48);

    public static Font BaseFont(float size = 10f, FontStyle style = FontStyle.Regular) =>
        new("Microsoft YaHei UI", size, style);

    public static Font TitleFont(float size = 15f) =>
        new("SimSun", size, FontStyle.Regular);

    public static Font BrandFont(float size = 32f) => new("KaiTi", size, FontStyle.Bold);

    public static int Scale(Control control, int value) => (int)Math.Round(value * control.DeviceDpi / 96f);

    public static void DrawText(Graphics graphics, string? text, Font? font, Rectangle bounds, Color color, TextFormatFlags flags) =>
        TextRenderer.DrawText(graphics, text, font, bounds, color, flags | TextFormatFlags.SingleLine |
            TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void EnableDarkWindow(Form form)
    {
        if (SystemInformation.HighContrast) return;
        void ApplyDwm()
        {
            if (!form.IsHandleCreated) return;
            int dark = 1;
            _ = DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
            int caption = ColorTranslator.ToWin32(Ink);
            int text = ColorTranslator.ToWin32(Paper);
            int border = ColorTranslator.ToWin32(Dim);
            _ = DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(form.Handle, 36, ref text, sizeof(int));
            _ = DwmSetWindowAttribute(form.Handle, 34, ref border, sizeof(int));
        }
        if (form.IsHandleCreated) ApplyDwm();
        else form.HandleCreated += (_, _) => ApplyDwm();
    }

    public static void Apply(Control control)
    {
        if (SystemInformation.HighContrast)
        {
            control.ForeColor = SystemColors.WindowText;
            control.BackColor = SystemColors.Window;
            foreach (Control child in control.Controls) Apply(child);
            return;
        }
        if (control is Form form)
        {
            EnableDarkWindow(form);
            form.BackColor = Ink;
            form.ForeColor = Paper;
        }
        else if (control is Label or Panel)
        {
            control.BackColor = Color.Transparent;
            if (control.ForeColor == SystemColors.ControlText) control.ForeColor = Paper;
        }
        else if (control is TextBox)
        {
            control.BackColor = Surface;
            control.ForeColor = Paper;
        }

        foreach (Control child in control.Controls) Apply(child);

        if (control is DataGridView grid)
        {
            StyleGrid(grid);
        }
        else if (control is StatusStrip status)
        {
            status.BackColor = Ink;
            status.ForeColor = Muted;
            status.SizingGrip = false;
            foreach (ToolStripItem item in status.Items)
                item.ForeColor = Muted;
        }
        else if (control is Button button and not (IconButton or TabButton or TextActionButton))
        {
            button.BackColor = Surface;
            button.ForeColor = Paper;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Line;
            button.FlatAppearance.MouseOverBackColor = Hover;
            button.FlatAppearance.MouseDownBackColor = Cinnabar;
        }
    }

    public static void StyleGrid(DataGridView grid)
    {
        if (SystemInformation.HighContrast) return;
        EnableDoubleBuffer(grid);
        grid.EnableHeadersVisualStyles = false;
        grid.BackgroundColor = Ink;
        grid.GridColor = Line;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface,
            ForeColor = Paper,
            SelectionBackColor = Surface,
            SelectionForeColor = Paper,
            Font = TitleFont(10f),
            Padding = new Padding(10, 0, 10, 0),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface,
            ForeColor = Paper,
            SelectionBackColor = Selected,
            SelectionForeColor = Paper,
            Font = BaseFont(10f),
            Padding = new Padding(10, 0, 10, 0),
        };
    }

    public static void EnableDoubleBuffer(Control control)
    {
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(control, true, null);
    }

    public static void DrawTogglePill(Graphics g, Rectangle bounds, bool isChecked, bool isEnabled, bool isHovered = false)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = g.DpiX / 96f;
        int w = (int)(36 * scale), h = (int)(19 * scale);
        int x = bounds.X + (bounds.Width - w) / 2;
        int y = bounds.Y + (bounds.Height - h) / 2;
        var rect = new Rectangle(x, y, w, h);

        Color trackColor;
        Color borderColor;
        Color knobColor;

        if (SystemInformation.HighContrast)
        {
            trackColor = isChecked ? SystemColors.Highlight : SystemColors.Control;
            borderColor = SystemColors.WindowText;
            knobColor = isChecked ? SystemColors.HighlightText : SystemColors.WindowText;
        }
        else if (!isEnabled)
        {
            trackColor = isChecked ? Color.FromArgb(95, 52, 45) : Color.FromArgb(35, 39, 33);
            borderColor = Color.FromArgb(46, 48, 43);
            knobColor = Dim;
        }
        else if (isChecked)
        {
            trackColor = isHovered ? CinnabarHover : Cinnabar;
            borderColor = trackColor;
            knobColor = Paper;
        }
        else
        {
            trackColor = isHovered ? Hover : Color.FromArgb(43, 48, 40);
            borderColor = isHovered ? Muted : Line;
            knobColor = isHovered ? Paper : Muted;
        }

        using var path = RoundedRect(rect, h / 2);
        using (var brush = new SolidBrush(trackColor))
            g.FillPath(brush, path);
        using (var pen = new Pen(borderColor, 1f))
            g.DrawPath(pen, path);

        int knobSize = (int)(14 * scale);
        int inset = (int)(3 * scale);
        int knobX = isChecked ? rect.Right - knobSize - inset : rect.X + inset;
        int knobY = rect.Y + (rect.Height - knobSize) / 2;
        using var knobBrush = new SolidBrush(knobColor);
        g.FillEllipse(knobBrush, knobX, knobY, knobSize, knobSize);
    }

    public static void DrawSelectionAccent(Graphics g, Rectangle rowBounds)
    {
        if (SystemInformation.HighContrast) return;
        using var brush = new SolidBrush(Cinnabar);
        g.FillRectangle(brush, rowBounds.X, rowBounds.Y + 1, 3, rowBounds.Height - 2);
    }

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class IconButton : InkButton
{
    private bool hovered;
    private bool pressed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Accent { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Borderless { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Caption { get; set; } = "";

    private readonly GameIcon icon;
    private readonly float iconSize;

    public IconButton(GameIcon icon, int width = 36, int height = 34, float iconSize = 18f)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        this.icon = icon;
        this.iconSize = iconSize;
        Font = GameTheme.BaseFont(8f);
        Size = new Size(width, height);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs mevent) { if (mevent.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(mevent); }
    protected override void OnMouseUp(MouseEventArgs mevent) { pressed = false; Invalidate(); base.OnMouseUp(mevent); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        if (SystemInformation.HighContrast)
        {
            g.Clear(SystemColors.Control);
            var iconBounds = Caption.Length == 0 ? ClientRectangle : new Rectangle(0, 0, Width, Height / 2);
            var color = Enabled ? SystemColors.ControlText : SystemColors.GrayText;
            GameIcons.Draw(g, icon, iconBounds, color, iconSize);
            if (Caption.Length != 0)
            {
                using var captionFont = GameTheme.BaseFont(8f);
                GameTheme.DrawText(g, Caption, captionFont, new Rectangle(0, Height / 2, Width, Height / 2), color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
            return;
        }

        Color bg = Accent
            ? (!Enabled ? GameTheme.Surface : pressed ? GameTheme.Cinnabar : hovered ? GameTheme.CinnabarHover : GameTheme.Cinnabar)
            : (!Enabled ? GameTheme.Surface : pressed ? GameTheme.Selected : hovered ? GameTheme.Hover : GameTheme.Surface);
        Color border = Accent
            ? (!Enabled ? GameTheme.Line : GameTheme.CinnabarHover)
            : (Focused ? GameTheme.Cinnabar : hovered && Enabled ? GameTheme.Muted : GameTheme.Line);
        Color fg = !Enabled ? GameTheme.Dim : GameTheme.Paper;

        if (!Borderless || hovered || pressed || Focused)
        {
            using var path = GameTheme.RoundedRect(rect, GameTheme.Scale(this, 3));
            using (var brush = new SolidBrush(bg)) g.FillPath(brush, path);
            if (!Borderless || Focused)
            {
                using var pen = new Pen(border, Focused ? 1.5f : 1f);
                g.DrawPath(pen, path);
            }
        }

        var glyphBounds = Caption.Length == 0 ? ClientRectangle : new Rectangle(0, GameTheme.Scale(this, 2), Width, Height / 2);
        GameIcons.Draw(g, icon, glyphBounds, fg, iconSize);
        if (Caption.Length != 0)
        {
            using var captionFont = GameTheme.BaseFont(8f);
            GameTheme.DrawText(g, Caption, captionFont, new Rectangle(0, Height / 2, Width, Height / 2), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}

internal sealed class TabButton : InkButton
{
    private bool hovered;
    private bool active;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => active;
        set { if (active == value) return; active = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public GameIcon Glyph { get; set; }

    public TabButton(GameIcon glyph = GameIcon.None)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Glyph = glyph;
        Font = GameTheme.TitleFont(11f);
        Size = new Size(172, 38);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 8, 0);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (SystemInformation.HighContrast)
        {
            using var fill = new SolidBrush(Active ? SystemColors.Highlight : SystemColors.Window);
            g.FillRectangle(fill, ClientRectangle);
            GameTheme.DrawText(g, Text, Font, ClientRectangle, Active ? SystemColors.HighlightText : SystemColors.WindowText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
            return;
        }

        var rect = new Rectangle(0, 0, Width - 1, Height);
        Color bg = Active ? Color.FromArgb(64, 39, 29) : hovered ? GameTheme.Hover : GameTheme.Surface;
        Color fg = Active ? GameTheme.Paper : hovered ? GameTheme.Paper : GameTheme.Muted;

        int slope = GameTheme.Scale(this, 10);
        using var shape = new GraphicsPath();
        shape.AddPolygon([new Point(0, rect.Bottom), new Point(0, 5), new Point(5, 0),
            new Point(rect.Right - slope, 0), new Point(rect.Right, rect.Bottom)]);
        using (var brush = new SolidBrush(bg)) g.FillPath(brush, shape);

        if (Active)
        {
            using var accentBrush = new SolidBrush(GameTheme.Cinnabar);
            g.FillRectangle(accentBrush, rect.X, rect.Bottom - 3, rect.Width + 1, 3);
        }
        else if (Focused && ShowFocusCues)
        {
            using var focusPen = new Pen(GameTheme.Cinnabar, 1f);
            g.DrawRectangle(focusPen, rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
        }

        int textInset = Glyph == GameIcon.None ? 0 : GameTheme.Scale(this, 32);
        if (Glyph != GameIcon.None)
        {
            GameIcons.Draw(g, Glyph, new Rectangle(GameTheme.Scale(this, 10), 0, textInset, Height - 2), fg, 18);
        }
        GameTheme.DrawText(g, Text, Font, new Rectangle(textInset, 0, Width - textInset - GameTheme.Scale(this, 6), Height - 2), fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -4, -4), fg, bg);
    }
}

internal sealed class ToggleSwitch : Control
{
    private bool isChecked;
    private bool hovered;

    public event EventHandler? CheckedChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            isChecked = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        Font = GameTheme.BaseFont(10f);
        Height = 32;
        Width = 220;
        Cursor = Cursors.Hand;
        TabStop = true;
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnTextChanged(EventArgs e) { AccessibleName = Text; Invalidate(); base.OnTextChanged(e); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (!Enabled) return;
        Focus();
        Checked = !Checked;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
        {
            Checked = !Checked;
            e.Handled = true;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) => InkArtwork.PaintBackground(this, e.Graphics);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int pillWidth = GameTheme.Scale(this, 42);
        var pillRect = new Rectangle(Width - pillWidth, 0, pillWidth, Height);
        var textRect = new Rectangle(0, 0, Math.Max(0, Width - pillWidth - GameTheme.Scale(this, 6)), Height);

        Color fg = SystemInformation.HighContrast
            ? (Enabled ? SystemColors.WindowText : SystemColors.GrayText)
            : (Enabled ? GameTheme.Paper : GameTheme.Dim);

        GameTheme.DrawText(g, Text, Font, textRect, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        GameTheme.DrawTogglePill(g, pillRect, isChecked, Enabled, hovered);

        if (Focused) ControlPaint.DrawFocusRectangle(g, ClientRectangle, fg, BackColor);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ToggleAccessibleObject(this);

    private sealed class ToggleAccessibleObject(ToggleSwitch owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State => base.State | (owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);
        public override void DoDefaultAction() { if (owner.Enabled) owner.Checked = !owner.Checked; }
    }
}

internal sealed class ThemedComboBox : ComboBox
{
    private const int WM_PAINT = 0x000F;

    public ThemedComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        ItemHeight = 24;
        Font = GameTheme.BaseFont(10f);
        BackColor = GameTheme.Surface;
        ForeColor = GameTheme.Paper;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        Color bg = SystemInformation.HighContrast
            ? (selected ? SystemColors.Highlight : SystemColors.Window)
            : (selected ? GameTheme.Selected : GameTheme.Surface);
        Color fg = SystemInformation.HighContrast
            ? (selected ? SystemColors.HighlightText : SystemColors.WindowText)
            : GameTheme.Paper;

        using (var brush = new SolidBrush(bg))
            e.Graphics.FillRectangle(brush, e.Bounds);

        if (selected && !SystemInformation.HighContrast)
        {
            using var accent = new SolidBrush(GameTheme.Cinnabar);
            e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);
        }

        var text = Items[e.Index]?.ToString() ?? "";
        var textBounds = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height);
        GameTheme.DrawText(e.Graphics, text, Font, textBounds, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_PAINT && !SystemInformation.HighContrast)
        {
            using var g = Graphics.FromHwnd(Handle);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var borderPen = new Pen(Focused ? GameTheme.Cinnabar : GameTheme.Line, 1f);
            g.DrawRectangle(borderPen, rect);

            int arrowWidth = GameTheme.Scale(this, 24);
            var arrowRect = new Rectangle(Width - arrowWidth, 1, arrowWidth - 1, Height - 2);
            using (var bgBrush = new SolidBrush(GameTheme.Surface))
                g.FillRectangle(bgBrush, arrowRect);
            GameIcons.Draw(g, GameIcon.Chevron, arrowRect, GameTheme.Muted, 14);
        }
    }
}
