using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EQOverwatch.Core.Export;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Theming;

namespace EQOverwatch.App.Services;

/// <summary>
/// Draws the "Copy Image for Discord" picture: the encounter summary, a table of every attacker (Total, SDPS, Max Hit, Max Crit,
/// Crit %) and the product footer, in the active theme's colors. Rendered at twice the layout size so it stays sharp when Discord
/// scales it. Call on the UI thread.
/// </summary>
public static class EncounterImageRenderer
{
    private const double Scale = 2.0;
    private const double Width = 760;
    private const double Padding = 22;
    private const double RowHeight = 28;
    private const double HeaderHeight = 74;
    private const double TableHeaderHeight = 26;
    private const double FooterHeight = 38;
    private const double SectionHeight = 40;

    private static readonly Typeface Regular = new("Segoe UI");
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    // Column left edges (name is left-aligned; the numbers are right-aligned to the next edge).
    private static readonly double[] ColumnRight = { 52, 0, 0, 400, 490, 580, 655, 716 };

    public static BitmapSource Render(EncounterSnapshot encounter, ThemePalette palette, string footer, int topCount = EncounterExport.AllAttackers)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        ArgumentNullException.ThrowIfNull(palette);

        IReadOnlyList<EncounterExportRow> rows = EncounterExport.BuildRows(encounter, topCount);
        IReadOnlyList<DamageTakenSnapshot> taken = encounter.TakenRows;
        double takenHeight = taken.Count == 0 ? 0 : SectionHeight + TableHeaderHeight + (taken.Count * RowHeight);
        double height = HeaderHeight + TableHeaderHeight + (Math.Max(1, rows.Count) * RowHeight) + takenHeight + FooterHeight;

        Brush window = Solid(palette.WindowBg);
        Brush card = Solid(palette.CardBg);
        Brush accent = Solid(palette.Accent);
        Brush text = Solid(palette.TextPrimary);
        Brush muted = MixBrush(palette.TextPrimary, palette.WindowBg, 0.45);
        Brush barFill = WithOpacity(palette.Accent, 0.38);
        Brush takenFill = WithOpacity(TakenColor, 0.38);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window, null, new Rect(0, 0, Width, height));
            dc.DrawRoundedRectangle(card, null, new Rect(10, 10, Width - 20, height - 20), 12, 12);
            dc.DrawRoundedRectangle(accent, null, new Rect(10, 10, 6, height - 20), 3, 3);

            // header
            Draw(dc, encounter.TargetName, Bold, 22, text, Padding + 6, 20, Width - (2 * Padding) - 6, TextAlignment.Left);
            Draw(dc, SummaryLine(encounter), Regular, 13, muted, Padding + 6, 52, Width - (2 * Padding) - 6, TextAlignment.Left);

            // table header
            double y = HeaderHeight;
            string[] headers = EncounterExport.ColumnHeaders.ToArray();
            DrawRow(dc, headers, y, Bold, 12, muted);
            y += TableHeaderHeight;
            dc.DrawRectangle(Solid(palette.TextPrimary, 0x30), null, new Rect(Padding, y - 2, Width - (2 * Padding), 1));

            // rows
            foreach (EncounterExportRow row in rows)
            {
                double barWidth = (Width - (2 * Padding)) * Math.Clamp(row.Fill, 0, 1);
                if (barWidth > 0)
                {
                    dc.DrawRoundedRectangle(barFill, null, new Rect(Padding, y + 2, barWidth, RowHeight - 4), 5, 5);
                }

                DrawRow(dc, new[] { row.Rank.ToString(CultureInfo.InvariantCulture), row.DisplayName, row.Class, row.Total, row.Sdps, row.MaxHit, row.MaxCrit, row.CritPercent },
                    y, Regular, 14, text);
                y += RowHeight;
            }

            if (rows.Count == 0)
            {
                Draw(dc, "No attackers recorded.", Regular, 14, muted, Padding + 6, y + 4, 400, TextAlignment.Left);
            }

            if (taken.Count > 0)
            {
                // Damage taken: what the fight's target did to the group, below the damage dealt.
                y += 10;
                Draw(dc, $"Damage taken from {encounter.TargetName}", Bold, 16, text, Padding + 6, y, 440, TextAlignment.Left);
                Draw(dc, $"{encounter.TotalDamageTaken.ToString("N0", CultureInfo.InvariantCulture)} total", Regular, 13, muted,
                    Width - Padding - 220, y + 2, 220, TextAlignment.Right);
                y += SectionHeight - 10;

                DrawTakenRow(dc, new[] { "#", "Name", "Taken", "% of total", "Hits", "Max Hit" }, y, Bold, 12, muted);
                y += TableHeaderHeight;
                dc.DrawRectangle(Solid(palette.TextPrimary, 0x30), null, new Rect(Padding, y - 2, Width - (2 * Padding), 1));

                long top = taken.Max(t => t.TotalDamage);
                for (int i = 0; i < taken.Count; i++)
                {
                    DamageTakenSnapshot t = taken[i];
                    double barWidth = top > 0 ? (Width - (2 * Padding)) * ((double)t.TotalDamage / top) : 0;
                    if (barWidth > 0)
                    {
                        dc.DrawRoundedRectangle(takenFill, null, new Rect(Padding, y + 2, barWidth, RowHeight - 4), 5, 5);
                    }

                    DrawTakenRow(dc, new[]
                    {
                        (i + 1).ToString(CultureInfo.InvariantCulture),
                        t.DisplayName,
                        t.TotalDamage.ToString("N0", CultureInfo.InvariantCulture),
                        t.PercentOfTotal.ToString("0.0", CultureInfo.InvariantCulture) + "%",
                        t.Hits.ToString("N0", CultureInfo.InvariantCulture),
                        t.MaxHit.ToString("N0", CultureInfo.InvariantCulture),
                    }, y, Regular, 14, text);
                    y += RowHeight;
                }
            }

            // footer
            Draw(dc, footer, Regular, 12, muted, Padding, height - FooterHeight + 4, Width - (2 * Padding), TextAlignment.Right);
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(Width * Scale), (int)Math.Ceiling(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static string SummaryLine(EncounterSnapshot encounter) =>
        $"{encounter.StartTime:yyyy-MM-dd HH:mm:ss}   •   {EncounterExport.FormatDuration(encounter.DurationSeconds)}   •   " +
        $"{encounter.TotalDamage.ToString("N0", CultureInfo.InvariantCulture)} damage   •   " +
        $"group DPS {Math.Round(encounter.TotalDamage / Math.Max(1.0, encounter.DurationSeconds)).ToString("N0", CultureInfo.InvariantCulture)}";

    private static void DrawRow(DrawingContext dc, IReadOnlyList<string> cells, double y, Typeface face, double size, Brush brush)
    {
        // # (right-aligned), Name and Class (left), then five right-aligned number columns.
        Draw(dc, cells[0], face, size, brush, Padding, y + 4, ColumnRight[0] - Padding, TextAlignment.Right);
        Draw(dc, cells[1], face, size, brush, 62, y + 4, 146, TextAlignment.Left);
        Draw(dc, cells[2], face, size, brush, 210, y + 4, 40, TextAlignment.Left);

        double left = 250;
        for (int i = 3; i < cells.Count; i++)
        {
            double right = ColumnRight[i];
            Draw(dc, cells[i], face, size, brush, left, y + 4, right - left, TextAlignment.Right);
            left = right;
        }
    }

    /// <summary>Warm color for the damage-taken bars, so they read apart from the damage-dealt bars.</summary>
    private const string TakenColor = "#E5845C";

    private static void DrawTakenRow(DrawingContext dc, IReadOnlyList<string> cells, double y, Typeface face, double size, Brush brush)
    {
        // # (right-aligned), Name (left, wide), then four right-aligned number columns.
        Draw(dc, cells[0], face, size, brush, Padding, y + 4, ColumnRight[0] - Padding, TextAlignment.Right);
        Draw(dc, cells[1], face, size, brush, 62, y + 4, 290, TextAlignment.Left);

        double[] rights = { 0, 0, 400, 490, 580, 655 };
        double left = 352;
        for (int i = 2; i < cells.Count; i++)
        {
            Draw(dc, cells[i], face, size, brush, left, y + 4, rights[i] - left, TextAlignment.Right);
            left = rights[i];
        }
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
