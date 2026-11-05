using System.Drawing.Drawing2D;

namespace PhantomBladeModManager;

internal enum GameIcon { None, Settings, Refresh, Restore, Delete, Folder, Layers, Keyboard, Document, Swords, Edit, Chevron }

internal static class GameIcons
{
    public static void Draw(Graphics graphics, GameIcon icon, Rectangle bounds, Color color, float size = 20)
    {
        if (icon == GameIcon.None) return;
        var state = graphics.Save();
        graphics.SetClip(bounds, CombineMode.Intersect);
        float edge = Math.Min(Math.Min(bounds.Width, bounds.Height), size * graphics.DpiX / 96f);
        graphics.TranslateTransform(bounds.X + (bounds.Width - edge) / 2, bounds.Y + (bounds.Height - edge) / 2);
        graphics.ScaleTransform(edge / 24, edge / 24);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        void Line(float x1, float y1, float x2, float y2) => graphics.DrawLine(pen, x1, y1, x2, y2);
        void Lines(params PointF[] points) => graphics.DrawLines(pen, points);
        switch (icon)
        {
            case GameIcon.Settings:
                var points = Enumerable.Range(0, 32).Select(i =>
                {
                    double angle = i * Math.PI / 16;
                    float radius = i % 4 is 0 or 3 ? 10 : 8;
                    return new PointF(12 + radius * (float)Math.Cos(angle), 12 + radius * (float)Math.Sin(angle));
                }).ToArray();
                graphics.DrawPolygon(pen, points);
                graphics.DrawEllipse(pen, 8, 8, 8, 8);
                break;
            case GameIcon.Refresh:
                graphics.DrawArc(pen, 4, 4, 16, 16, 45, 280);
                Lines(new(15, 3), new(20, 5), new(19, 10));
                break;
            case GameIcon.Restore:
                graphics.DrawArc(pen, 4, 4, 16, 16, 215, 285);
                Lines(new(3, 3), new(3, 9), new(9, 9));
                break;
            case GameIcon.Delete:
                graphics.DrawRectangle(pen, 7, 6, 10, 15);
                Line(5, 6, 19, 6); Lines(new(9, 6), new(9, 3), new(15, 3), new(15, 6));
                Line(10, 10, 10, 17); Line(14, 10, 14, 17);
                break;
            case GameIcon.Folder:
                graphics.DrawPolygon(pen, [new(3, 6), new(9, 6), new(11, 9), new(21, 9), new(21, 20), new(3, 20)]);
                break;
            case GameIcon.Layers:
                graphics.DrawPolygon(pen, [new(12, 3), new(22, 8), new(12, 13), new(2, 8)]);
                Lines(new(3, 12), new(12, 17), new(21, 12));
                Lines(new(3, 16), new(12, 21), new(21, 16));
                break;
            case GameIcon.Keyboard:
                graphics.DrawRectangle(pen, 2, 5, 20, 14);
                for (int row = 0; row < 2; row++)
                    for (int col = 0; col < 5; col++) Line(5 + col * 3.5f, 9 + row * 3, 5.5f + col * 3.5f, 9 + row * 3);
                Line(7, 16, 17, 16);
                break;
            case GameIcon.Document:
                graphics.DrawPolygon(pen, [new(5, 2), new(15, 2), new(20, 7), new(20, 22), new(5, 22)]);
                Lines(new(15, 2), new(15, 7), new(20, 7));
                Line(9, 11, 16, 11); Line(9, 15, 16, 15); Line(9, 18, 13, 18);
                break;
            case GameIcon.Swords:
                Lines(new(5, 3), new(5, 7), new(18, 20));
                Lines(new(19, 3), new(19, 7), new(6, 20));
                Line(13, 19, 18, 14); Line(6, 14, 11, 19);
                break;
            case GameIcon.Edit:
                graphics.DrawPolygon(pen, [new(5, 16), new(16, 5), new(20, 9), new(9, 20), new(4, 21)]);
                Line(14, 7, 18, 11);
                break;
            case GameIcon.Chevron:
                Lines(new(7, 10), new(12, 15), new(17, 10));
                break;
        }
        graphics.Restore(state);
    }
}
