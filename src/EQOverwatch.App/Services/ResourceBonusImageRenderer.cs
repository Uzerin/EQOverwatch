using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Theming;

namespace EQOverwatch.App.Services;

/// <summary>
/// Draws the Resource Hunter picture for Discord: the server and day, then the day's tables two by two (each bonus with its zones),
/// in the active theme's colors. Rendered at twice the layout size so it stays sharp. Call on the UI thread.
/// </summary>
public static class ResourceBonusImageRenderer
{
    private const double Scale = 2.0;
    private const double Width = 760;
    private const double Padding = 24;
    private const double ColumnGap = 24;
    private const double RowHeight = 22;
    private const double TitleHeight = 28;
    private const double HeaderHeight = 64;
    private const double FooterHeight = 36;
    private const double GroupGap = 14;

    private static readonly Typeface Regular = new("Segoe UI");
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static BitmapSource Render(string title, IReadOnlyList<ResourcePairView> pairs, ThemePalette palette, string footer)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(palette);

        static double GroupHeight(ResourceGroupView? g) => g is null ? 0 : TitleHeight + (g.Rows.Count * RowHeight);
        double body = pairs.Sum(p => Math.Max(GroupHeight(p.Left), GroupHeight(p.Right)) + GroupGap);
        double height = HeaderHeight + Math.Max(RowHeight, body) + FooterHeight;
        double column = (Width - (2 * Padding) - ColumnGap) / 2;

        Brush window = Solid(palette.WindowBg);
        Brush card = Solid(palette.CardBg);
        Brush accent = Solid(palette.Accent);
        Brush text = Solid(palette.TextPrimary);
        Brush muted = Mix(palette.TextPrimary, palette.WindowBg, 0.45);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window, null, new Rect(0, 0, Width, height));
            dc.DrawRoundedRectangle(card, null, new Rect(10, 10, Width - 20, height - 20), 12, 12);
            Draw(dc, title, Bold, 20, text, Padding, 22, Width - (2 * Padding));

            double y = HeaderHeight;
            foreach (ResourcePairView pair in pairs)
            {
                DrawGroup(dc, pair.Left, Padding, y, column, accent, text);
                DrawGroup(dc, pair.Right, Padding + column + ColumnGap, y, column, accent, text);
                y += Math.Max(GroupHeight(pair.Left), GroupHeight(pair.Right)) + GroupGap;
            }

            if (pairs.Count == 0)
            {
                Draw(dc, "No bonuses known yet.", Regular, 14, muted, Padding, y, 400);
            }

            Draw(dc, footer, Regular, 12, muted, Padding, height - FooterHeight + 4, Width - (2 * Padding), TextAlignment.Right);
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(Width * Scale), (int)Math.Ceiling(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawGroup(DrawingContext dc, ResourceGroupView? group, double x, double y, double width, Brush accent, Brush text)
    {
        if (group is null)
        {
            return;
        }

        Draw(dc, group.Title, Bold, 15, accent, x, y + 2, width);
        double row = y + TitleHeight;
        foreach (ResourceZoneView zone in group.Rows)
        {
            Draw(dc, zone.Zone, Regular, 13, text, x + 8, row, width - 8);
            row += RowHeight;
        }
    }

    private static void Draw(DrawingContext dc, string text, Typeface face, double size, Brush brush, double x, double y, double maxWidth,
        TextAlignment alignment = TextAlignment.Left)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, Scale)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment,
        };

        dc.DrawText(formatted, new Point(x, y));
    }

    private static Brush Solid(string hex)
    {
        Rgba color = ThemeColors.TryParse(hex, out Rgba parsed) ? parsed : ThemeColors.Black;
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static Brush Mix(string fromHex, string toHex, double amount)
    {
        Rgba from = ThemeColors.TryParse(fromHex, out Rgba a) ? a : ThemeColors.White;
        Rgba to = ThemeColors.TryParse(toHex, out Rgba b) ? b : ThemeColors.Black;
        Rgba mixed = ThemeColors.Blend(from, to, amount);
        var brush = new SolidColorBrush(Color.FromArgb(mixed.A, mixed.R, mixed.G, mixed.B));
        brush.Freeze();
        return brush;
    }
}
