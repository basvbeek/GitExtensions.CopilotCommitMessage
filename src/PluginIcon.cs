using System.Drawing;
using System.Drawing.Drawing2D;

namespace GitExtensions.CopilotCommitMessage;

internal static class PluginIcon
{
    internal static Bitmap Create()
    {
        Bitmap icon = new(32, 32);
        using Graphics graphics = Graphics.FromImage(icon);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        PointF[] page =
        [
            new(5, 3),
            new(18, 3),
            new(25, 10),
            new(25, 28),
            new(5, 28),
        ];
        using SolidBrush pageBrush = new(Color.FromArgb(248, 251, 255));
        using Pen outline = new(Color.FromArgb(32, 55, 78), 1.5f);
        graphics.FillPolygon(pageBrush, page);
        graphics.DrawPolygon(outline, page);

        PointF[] foldedCorner =
        [
            new(18, 3),
            new(18, 10),
            new(25, 10),
        ];
        using SolidBrush foldBrush = new(Color.FromArgb(183, 220, 237));
        graphics.FillPolygon(foldBrush, foldedCorner);
        graphics.DrawLine(outline, 18, 3, 18, 10);
        graphics.DrawLine(outline, 18, 10, 25, 10);

        using Pen textLine = new(Color.FromArgb(52, 94, 121), 1.5f);
        graphics.DrawLine(textLine, 9, 14, 20, 14);
        graphics.DrawLine(textLine, 9, 18, 18, 18);
        graphics.DrawLine(textLine, 9, 22, 15, 22);

        using SolidBrush badgeBrush = new(Color.FromArgb(0, 130, 139));
        using Pen badgeOutline = new(Color.White, 1.25f);
        graphics.FillEllipse(badgeBrush, 18, 18, 12, 12);
        graphics.DrawEllipse(badgeOutline, 18, 18, 12, 12);

        PointF[] sparkle =
        [
            new(24, 20.5f),
            new(24.9f, 23.1f),
            new(27.5f, 24),
            new(24.9f, 24.9f),
            new(24, 27.5f),
            new(23.1f, 24.9f),
            new(20.5f, 24),
            new(23.1f, 23.1f),
        ];
        graphics.FillPolygon(Brushes.White, sparkle);

        return icon;
    }
}