using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PhantomBladeModManager;

internal static class InkArtwork
{
    private static readonly Lazy<Task<Bitmap>> Loading = new(LoadAsync);
    public static Bitmap? Image { get; private set; }

    public static async Task InitializeAsync() => Image = await Loading.Value;

    private static async Task<Bitmap> LoadAsync()
    {
        await using var source = typeof(InkArtwork).Assembly.GetManifestResourceStream(
            "PhantomBladeModManager.Assets.ink-landscape.png")!;
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer);
        buffer.Position = 0;
        return await Task.Run(() =>
        {
            using var decoded = System.Drawing.Image.FromStream(buffer);
            return new Bitmap(decoded);
        });
    }

    public static void Draw(Graphics graphics, Rectangle bounds, bool landscape)
    {
        if (Image == null || SystemInformation.HighContrast) return;
        var source = landscape ? new Rectangle(0, 0, Image.Width, Image.Height)
            : new Rectangle(0, Image.Height / 3, Image.Width, Image.Height * 2 / 3);
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = landscape ? 0.68f : 0.8f });
        graphics.DrawImage(Image, bounds, source.X, source.Y, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
    }

    public static void PaintBackground(Control control, Graphics graphics)
    {
        using var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Window : GameTheme.Ink);
        graphics.FillRectangle(fill, control.ClientRectangle);
        if (control.FindForm() is not InkForm form) return;
        var offset = Point.Empty;
        for (Control? current = control; current != null && current != form; current = current.Parent)
            offset.Offset(current.Left, current.Top);
        Draw(graphics, new Rectangle(-offset.X, -offset.Y, form.ClientSize.Width, form.ClientSize.Height), form.LandscapeHeader);
    }
}

internal sealed class InkLayoutPanel : TableLayoutPanel
{
    public InkLayoutPanel() => DoubleBuffered = true;
    protected override void OnPaintBackground(PaintEventArgs e) => InkArtwork.PaintBackground(this, e.Graphics);
}

internal sealed class InkFlowPanel : FlowLayoutPanel
{
    public InkFlowPanel() => DoubleBuffered = true;
    protected override void OnPaintBackground(PaintEventArgs e) => InkArtwork.PaintBackground(this, e.Graphics);
}

internal class InkLabel : Label
{
    protected override void OnPaintBackground(PaintEventArgs e) => InkArtwork.PaintBackground(this, e.Graphics);
}

internal sealed class InkPathLabel : InkLabel
{
    protected override void OnPaint(PaintEventArgs e) =>
        GameTheme.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
}

internal abstract class InkButton : Button
{
    protected InkButton()
    {
        // ButtonBase is opaque by default, which skips OnPaintBackground and
        // leaves stale pixels in the shared buffer around our custom content.
        SetStyle(ControlStyles.Opaque, false);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => InkArtwork.PaintBackground(this, e.Graphics);
}

internal class InkForm : Form
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool LandscapeHeader { get; set; }

    public InkForm()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        ShowIcon = false;
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : GameTheme.Ink;
        ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : GameTheme.Paper;
    }

    protected void CompleteLayout()
    {
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(true);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await InkArtwork.InitializeAsync();
        if (!IsDisposed) Invalidate(true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        InkArtwork.Draw(e.Graphics, ClientRectangle, LandscapeHeader);
    }
}

internal sealed class InkGrid : DataGridView
{
    public InkGrid() => DoubleBuffered = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int LogicalRowHeight { get; set; } = 54;

    private void UpdateRowHeight()
    {
        RowTemplate.Height = GameTheme.Scale(this, LogicalRowHeight);
        foreach (DataGridViewRow row in Rows) row.Height = RowTemplate.Height;
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateRowHeight(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateRowHeight(); }

    protected override void PaintBackground(Graphics graphics, Rectangle clipBounds, Rectangle gridBounds)
    {
        base.PaintBackground(graphics, clipBounds, gridBounds);
        InkArtwork.Draw(graphics, ClientRectangle, false);
    }
}

internal sealed class BorderedPanel : Panel
{
    public BorderedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Padding = new Padding(1);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => InkArtwork.PaintBackground(this, e.Graphics);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : GameTheme.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

internal sealed class SettingsHeading : Label
{
    public SettingsHeading()
    {
        Font = GameTheme.TitleFont(10.5f);
        Dock = DockStyle.Fill;
        BackColor = Color.Transparent;
        TextAlign = ContentAlignment.MiddleLeft;
        Margin = new Padding(0, 10, 0, 0);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => InkArtwork.PaintBackground(this, e.Graphics);

    protected override void OnPaint(PaintEventArgs e)
    {
        int inset = GameTheme.Scale(this, 12);
        var color = SystemInformation.HighContrast ? SystemColors.WindowText : GameTheme.Paper;
        var size = TextRenderer.MeasureText(e.Graphics, Text, Font);
        using var pen = new Pen(SystemInformation.HighContrast ? color : GameTheme.Line);
        using var accent = new Pen(SystemInformation.HighContrast ? color : GameTheme.Muted);
        e.Graphics.DrawLine(accent, 1, Height / 2 + 5, 1, Height / 2 - 5);
        if (Width > size.Width + inset * 2)
            e.Graphics.DrawLine(pen, size.Width + inset * 2, Height / 2, Width - 1, Height / 2);
        GameTheme.DrawText(e.Graphics, Text, Font, new Rectangle(inset, 0, Width - inset, Height), color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

// Text labels follow the reference's explicit Save / Cancel / Defaults actions.
internal sealed class TextActionButton : InkButton
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Accent { get; set; }

    public TextActionButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(80, 30);
        Font = GameTheme.BaseFont(9f);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        bool hover = ClientRectangle.Contains(PointToClient(MousePosition));
        var fill = Accent && Enabled ? (hover ? GameTheme.Cinnabar : Color.FromArgb(102, 53, 39))
            : hover && Enabled ? GameTheme.Hover : GameTheme.Surface;
        var border = Focused || Accent && Enabled ? GameTheme.CinnabarHover : GameTheme.Dim;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = GameTheme.RoundedRect(bounds, GameTheme.Scale(this, 3));
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(border);
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(pen, path);
        GameTheme.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? GameTheme.Paper : GameTheme.Dim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -4, -4));
    }
}
