using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Theming;

namespace EQOverwatch.App.Services;

/// <summary>
/// Draws the "every kill" picture for Discord: the boss, a summary line (kills, fastest, average, best DPS) and one row for every
/// kill, newest first, in the active theme's colors. Rendered at twice the layout size so it stays sharp. Call on the UI thread.
/// </summary>
public static class BossKillsImageRenderer
{
    private const double Scale = 2.0;
    private const double Width = 760;
    private const double Padding = 22;
    private const double RowHeight = 28;
    private const double HeaderHeight = 74;
    private const double TableHeaderHeight = 26;
    private const double FooterHeight = 38;

    private const string FastestColor = "#7EE0A1";
    private const string BestDpsColor = "#F5C46B";

    private static readonly Typeface Regular = new("Segoe UI");
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    // Right edges of the number columns; "#" ends at 52 and the date starts at 62.
    private const double ClearRight = 400;
    private const double DpsRight = 500;
    private const double PlayersRight = 590;
    private const double DeathsRight = 680;

    public static BitmapSource Render(BossKillReport report, ThemePalette palette, string footer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(palette);

        IReadOnlyList<BossFight> kills = report.Kills;
        double height = HeaderHeight + TableHeaderHeight + (Math.Max(1, kills.Count) * RowHeight) + FooterHeight;

        Brush window = Solid(palette.WindowBg);
        Brush card = Solid(palette.CardBg);
        Brush accent = Solid(palette.Accent);
        Brush text = Solid(palette.TextPrimary);
        Brush muted = MixBrush(palette.TextPrimary, palette.WindowBg, 0.45);
        Brush barFill = WithOpacity(palette.Accent, 0.38);
        Brush fastest = Solid(FastestColor);
        Brush best = Solid(BestDpsColor);

        double fastestSeconds = kills.Count == 0 ? 0 : kills.Min(k => k.Seconds);
        double bestDps = kills.Count == 0 ? 0 : kills.Max(k => k.Dps);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window, null, new Rect(0, 0, Width, height));
            dc.DrawRoundedRectangle(card, null, new Rect(10, 10, Width - 20, height - 20), 12, 12);
            dc.DrawRoundedRectangle(accent, null, new Rect(10, 10, 6, height - 20), 3, 3);

            Draw(dc, $"{report.Boss}: every kill", Bold, 22, text, Padding + 6, 20, Width - (2 * Padding) - 6, TextAlignment.Left);
            Draw(dc, SummaryLine(report), Regular, 13, muted, Padding + 6, 52, Width - (2 * Padding) - 6, TextAlignment.Left);

            double y = HeaderHeight;
            DrawRow(dc, new[] { "#", "Started", "Clear time", "DPS", "Players", "Deaths" }, y, Bold, 12, muted, null, null);
            y += TableHeaderHeight;
            dc.DrawRectangle(Solid(palette.TextPrimary, 0x30), null, new Rect(Padding, y - 2, Width - (2 * Padding), 1));

            for (int i = 0; i < kills.Count; i++)
            {
                BossFight kill = kills[i];
                double barWidth = bestDps > 0 ? (Width - (2 * Padding)) * (kill.Dps / bestDps) : 0;
                if (barWidth > 0)
                {
                    dc.DrawRoundedRectangle(barFill, null, new Rect(Padding, y + 2, barWidth, RowHeight - 4), 5, 5);
                }

                DrawRow(dc, new[]
                {
                    (i + 1).ToString(CultureInfo.InvariantCulture),
                    kill.Start.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture),
                    BossHistoryStore.FormatDuration(kill.Seconds),
                    Math.Round(kill.Dps).ToString("N0", CultureInfo.InvariantCulture),
                    kill.Players.ToString(CultureInfo.InvariantCulture),
                    kill.Deaths.ToString(CultureInfo.InvariantCulture),
                }, y, Regular, 14, text,
                    kills.Count > 1 && Math.Abs(kill.Seconds - fastestSeconds) < 0.001 ? fastest : null,
                    kills.Count > 1 && Math.Abs(kill.Dps - bestDps) < 0.001 ? best : null);
                y += RowHeight;
            }

            if (kills.Count == 0)
            {
                Draw(dc, "No kills recorded yet.", Regular, 14, muted, Padding + 6, y + 4, 400, TextAlignment.Left);
            }

            Draw(dc, footer, Regular, 12, muted, Padding, height - FooterHeight + 4, Width - (2 * Padding), TextAlignment.Right);
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(Width * Scale), (int)Math.Ceiling(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static string SummaryLine(BossKillReport report) =>
        $"{report.Kills.Count:N0} kill{(report.Kills.Count == 1 ? string.Empty : "s")}   •   fastest {BossHistoryStore.FormatDuration(report.FastestSeconds)}   •   " +
        $"average {BossHistoryStore.FormatDuration(report.AverageSeconds)}   •   best DPS {Math.Round(report.BestDps).ToString("N0", CultureInfo.InvariantCulture)}";

    private static void DrawRow(
        DrawingContext dc, IReadOnlyList<string> cells, double y, Typeface face, double size, Brush brush, Brush? clearBrush, Brush? dpsBrush)
    {
        Draw(dc, cells[0], face, size, brush, Padding, y + 4, 30, TextAlignment.Right);
        Draw(dc, cells[1], face, size, brush, 62, y + 4, 230, TextAlignment.Left);
        Draw(dc, cells[2], clearBrush is null ? face : Bold, size, clearBrush ?? brush, 292, y + 4, ClearRight - 292, TextAlignment.Right);
        Draw(dc, cells[3], dpsBrush is null ? face : Bold, size, dpsBrush ?? brush, ClearRight, y + 4, DpsRight - ClearRight, TextAlignment.Right);
        Draw(dc, cells[4], face, size, brush, DpsRight, y + 4, PlayersRight - DpsRight, TextAlignment.Right);
        Draw(dc, cells[5], face, size, brush, PlayersRight, y + 4, DeathsRight - PlayersRight, TextAlignment.Right);
    }

    private static void Draw(DrawingContext dc, string text, Typeface face, double size, Brush brush, double x, double y, double maxWidth, TextAlignment alignment)
    {
        var formatted = new FormattedText(
            text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, Scale)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment,
        };

        dc.DrawText(formatted, new Point(x, y));
    }

    private static Brush Solid(string hex, byte? alpha = null)
    {
        Rgba color = ThemeColors.TryParse(hex, out Rgba parsed) ? parsed : ThemeColors.Black;
        if (alpha is { } a)
        {
            color = color with { A = a };
        }

        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static Brush WithOpacity(string hex, double opacity) => Solid(hex, (byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)));

    private static Brush MixBrush(string fromHex, string toHex, double amount)
    {
        Rgba from = ThemeColors.TryParse(fromHex, out Rgba a) ? a : ThemeColors.White;
        Rgba to = ThemeColors.TryParse(toHex, out Rgba b) ? b : ThemeColors.Black;
        Rgba mixed = ThemeColors.Blend(from, to, amount);
        var brush = new SolidColorBrush(Color.FromArgb(mixed.A, mixed.R, mixed.G, mixed.B));
        brush.Freeze();
        return brush;
    }
}
