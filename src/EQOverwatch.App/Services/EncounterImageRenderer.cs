using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EQOverwatch.Core.Export;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Theming;

namespace EQOverwatch.App.Services;

/// <summary>
/// Draws the "Copy Image for Discord" picture in two halves under the encounter summary. The left half is the damage dealt (Total, SDPS,
/// Max Hit, Max Crit, Crit %) with every attacker. The right half stacks tanking (damage taken), healing and the deaths. Every character is
/// listed, the product footer runs along the bottom, and everything uses the active theme's colors. Rendered at twice the layout size so it
/// stays sharp when Discord scales it. Call on the UI thread.
/// </summary>
public static class EncounterImageRenderer
{
    private const double Scale = 2.0;
    private const double HalfWidth = 760;
    private const double Width = HalfWidth * 2;
    private const double Padding = 22;
    private const double RowHeight = 28;
    private const double HeaderHeight = 74;
    private const double TableHeaderHeight = 26;
    private const double FooterHeight = 38;
    private const double SectionHeight = 40;
    private const double SectionGap = 10;
    private const double TitleHeight = SectionHeight - SectionGap;

    private static readonly Typeface Regular = new("Segoe UI");
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    // Column left edges (name is left-aligned; the numbers are right-aligned to the next edge).
    private static readonly double[] ColumnRight = { 52, 0, 0, 400, 490, 580, 655, 716 };

    /// <summary>The picture as PNG bytes (for posting to Discord). Call on the UI thread.</summary>
    public static byte[] RenderPng(EncounterSnapshot encounter, ThemePalette palette, string footer, int topCount = EncounterExport.AllAttackers)
    {
        BitmapSource image = Render(encounter, palette, footer, topCount);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new System.IO.MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static BitmapSource Render(EncounterSnapshot encounter, ThemePalette palette, string footer, int topCount = EncounterExport.AllAttackers)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        ArgumentNullException.ThrowIfNull(palette);

        IReadOnlyList<EncounterExportRow> rows = EncounterExport.BuildRows(encounter, topCount);
        IReadOnlyList<DamageTakenSnapshot> taken = encounter.TakenRows;
        IReadOnlyList<HealingSnapshot> healing = encounter.HealingRows;
        IReadOnlyList<DeathSnapshot> deaths = encounter.DeathRows;

        double leftHeight = TitleHeight + TableHeaderHeight + (Math.Max(1, rows.Count) * RowHeight);
        double rightHeight = 0;
        if (taken.Count > 0)
        {
            rightHeight += TitleHeight + TableHeaderHeight + (taken.Count * RowHeight) + SectionGap;
        }

        if (healing.Count > 0)
        {
            rightHeight += TitleHeight + TableHeaderHeight + (healing.Count * RowHeight) + SectionGap;
        }

        rightHeight += TitleHeight + (deaths.Count == 0 ? RowHeight : TableHeaderHeight + (deaths.Count * RowHeight));
        double height = HeaderHeight + Math.Max(leftHeight, rightHeight) + FooterHeight;

        Brush window = Solid(palette.WindowBg);
        Brush card = Solid(palette.CardBg);
        Brush accent = Solid(palette.Accent);
        Brush text = Solid(palette.TextPrimary);
        Brush muted = MixBrush(palette.TextPrimary, palette.WindowBg, 0.45);
        Brush barFill = WithOpacity(palette.Accent, 0.38);
        Brush takenFill = WithOpacity(TakenColor, 0.38);
        Brush healFill = WithOpacity(HealingColor, 0.38);
        Brush line = Solid(palette.TextPrimary, 0x30);
        double contentWidth = HalfWidth - (2 * Padding);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window, null, new Rect(0, 0, Width, height));
            dc.DrawRoundedRectangle(card, null, new Rect(10, 10, Width - 20, height - 20), 12, 12);
            dc.DrawRoundedRectangle(accent, null, new Rect(10, 10, 6, height - 20), 3, 3);

            // header, across both halves
            Draw(dc, encounter.TargetName, Bold, 22, text, Padding + 6, 20, Width - (2 * Padding) - 6, TextAlignment.Left);
            Draw(dc, SummaryLine(encounter), Regular, 13, muted, Padding + 6, 52, Width - (2 * Padding) - 6, TextAlignment.Left);

            // a thin divider between the halves
            dc.DrawRectangle(Solid(palette.TextPrimary, 0x20), null, new Rect(HalfWidth - 0.5, HeaderHeight + 4, 1, Math.Max(0, height - HeaderHeight - FooterHeight - 4)));

            // ── left half: damage dealt ──
            double y = HeaderHeight;
            Draw(dc, "DPS", Bold, 16, text, Padding + 6, y, 440, TextAlignment.Left);
            Draw(dc, $"{encounter.TotalDamage.ToString("N0", CultureInfo.InvariantCulture)} total", Regular, 13, muted,
                HalfWidth - Padding - 220, y + 2, 220, TextAlignment.Right);
            y += SectionHeight - SectionGap;

            DrawRow(dc, EncounterExport.ColumnHeaders.ToArray(), y, Bold, 12, muted);
            y += TableHeaderHeight;
            dc.DrawRectangle(line, null, new Rect(Padding, y - 2, contentWidth, 1));

            foreach (EncounterExportRow row in rows)
            {
                double barWidth = contentWidth * Math.Clamp(row.Fill, 0, 1);
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

            // ── right half: tanking, healing, deaths ──
            dc.PushTransform(new TranslateTransform(HalfWidth, 0));
            double ry = HeaderHeight;

            if (taken.Count > 0)
            {
                // Damage taken: what the fight's target did to the group.
                Draw(dc, $"Tanking: damage taken from {encounter.TargetName}", Bold, 16, text, Padding + 6, ry, 440, TextAlignment.Left);
                Draw(dc, $"{encounter.TotalDamageTaken.ToString("N0", CultureInfo.InvariantCulture)} total", Regular, 13, muted,
                    HalfWidth - Padding - 220, ry + 2, 220, TextAlignment.Right);
                ry += SectionHeight - SectionGap;

                DrawTakenRow(dc, new[] { "#", "Name", "Taken", "% of total", "Hits", "Max Hit" }, ry, Bold, 12, muted);
                ry += TableHeaderHeight;
                dc.DrawRectangle(line, null, new Rect(Padding, ry - 2, contentWidth, 1));

                long top = taken.Max(t => t.TotalDamage);
                for (int i = 0; i < taken.Count; i++)
                {
                    DamageTakenSnapshot t = taken[i];
                    double barWidth = top > 0 ? contentWidth * ((double)t.TotalDamage / top) : 0;
                    if (barWidth > 0)
                    {
                        dc.DrawRoundedRectangle(takenFill, null, new Rect(Padding, ry + 2, barWidth, RowHeight - 4), 5, 5);
                    }

                    DrawTakenRow(dc, new[]
                    {
                        (i + 1).ToString(CultureInfo.InvariantCulture),
                        t.DisplayName,
                        t.TotalDamage.ToString("N0", CultureInfo.InvariantCulture),
                        t.PercentOfTotal.ToString("0.0", CultureInfo.InvariantCulture) + "%",
                        t.Hits.ToString("N0", CultureInfo.InvariantCulture),
                        t.MaxHit.ToString("N0", CultureInfo.InvariantCulture),
                    }, ry, Regular, 14, text);
                    ry += RowHeight;
                }

                ry += SectionGap;
            }

            if (healing.Count > 0)
            {
                long healTotal = healing.Sum(h => h.Effective);
                Draw(dc, "Healing", Bold, 16, text, Padding + 6, ry, 440, TextAlignment.Left);
                Draw(dc, $"{healTotal.ToString("N0", CultureInfo.InvariantCulture)} total", Regular, 13, muted,
                    HalfWidth - Padding - 220, ry + 2, 220, TextAlignment.Right);
                ry += SectionHeight - SectionGap;

                DrawHealRow(dc, new[] { "#", "Name", "Healed", "% of total", "HPS", "Heals", "Max Heal" }, ry, Bold, 12, muted);
                ry += TableHeaderHeight;
                dc.DrawRectangle(line, null, new Rect(Padding, ry - 2, contentWidth, 1));

                long topHeal = healing.Max(h => h.Effective);
                for (int i = 0; i < healing.Count; i++)
                {
                    HealingSnapshot h = healing[i];
                    double barWidth = topHeal > 0 ? contentWidth * ((double)h.Effective / topHeal) : 0;
                    if (barWidth > 0)
                    {
                        dc.DrawRoundedRectangle(healFill, null, new Rect(Padding, ry + 2, barWidth, RowHeight - 4), 5, 5);
                    }

                    DrawHealRow(dc, new[]
                    {
                        (i + 1).ToString(CultureInfo.InvariantCulture),
                        h.Name,
                        h.Effective.ToString("N0", CultureInfo.InvariantCulture),
                        (healTotal > 0 ? 100.0 * h.Effective / healTotal : 0).ToString("0.0", CultureInfo.InvariantCulture) + "%",
                        Math.Round(h.Hps).ToString("N0", CultureInfo.InvariantCulture),
                        h.Heals.ToString("N0", CultureInfo.InvariantCulture),
                        h.MaxHeal.ToString("N0", CultureInfo.InvariantCulture),
                    }, ry, Regular, 14, text);
                    ry += RowHeight;
                }

                ry += SectionGap;
            }

            // Deaths: who died and when in the fight. Always shown, so the layout is the same from fight to fight.
            Draw(dc, "Deaths", Bold, 16, text, Padding + 6, ry, 440, TextAlignment.Left);
            Draw(dc, deaths.Count == 0 ? "none" : $"{deaths.Count.ToString("N0", CultureInfo.InvariantCulture)} total", Regular, 13, muted,
                HalfWidth - Padding - 220, ry + 2, 220, TextAlignment.Right);
            ry += SectionHeight - SectionGap;

            if (deaths.Count == 0)
            {
                Draw(dc, "No deaths.", Regular, 14, muted, Padding + 6, ry + 4, 400, TextAlignment.Left);
            }
            else
            {
                DrawDeathRow(dc, new[] { "#", "Name", "Time", "Killed by" }, ry, Bold, 12, muted);
                ry += TableHeaderHeight;
                dc.DrawRectangle(line, null, new Rect(Padding, ry - 2, contentWidth, 1));

                for (int i = 0; i < deaths.Count; i++)
                {
                    DeathSnapshot d = deaths[i];
                    DrawDeathRow(dc, new[]
                    {
                        (i + 1).ToString(CultureInfo.InvariantCulture),
                        d.Name,
                        EncounterExport.FormatDuration((d.Time - encounter.StartTime).TotalSeconds),
                        string.IsNullOrWhiteSpace(d.KilledBy) ? "—" : d.KilledBy,
                    }, ry, Regular, 14, text);
                    ry += RowHeight;
                }
            }

            dc.Pop();

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

    /// <summary>Green for the healing bars.</summary>
    private const string HealingColor = "#5CC98A";

    private static void DrawTakenRow(DrawingContext dc, IReadOnlyList<string> cells, double y, Typeface face, double size, Brush brush) =>
        DrawNumberRow(dc, cells, y, face, size, brush, 300, new[] { 400.0, 490, 580, 655 });

    private static void DrawHealRow(DrawingContext dc, IReadOnlyList<string> cells, double y, Typeface face, double size, Brush brush) =>
        DrawNumberRow(dc, cells, y, face, size, brush, 240, new[] { 335.0, 425, 515, 610, 716 });

    /// <summary># (right-aligned), Name and Killed by (left), and the time into the fight (right-aligned).</summary>
    private static void DrawDeathRow(DrawingContext dc, IReadOnlyList<string> cells, double y, Typeface face, double size, Brush brush)
    {
        Draw(dc, cells[0], face, size, brush, Padding, y + 4, ColumnRight[0] - Padding, TextAlignment.Right);
        Draw(dc, cells[1], face, size, brush, 62, y + 4, 230, TextAlignment.Left);
        Draw(dc, cells[2], face, size, brush, 296, y + 4, 90, TextAlignment.Right);
        Draw(dc, cells[3], face, size, brush, 410, y + 4, 306, TextAlignment.Left);
    }

    /// <summary># (right-aligned), Name (left, as wide as the first number column allows), then right-aligned number columns.</summary>
    private static void DrawNumberRow(
        DrawingContext dc, IReadOnlyList<string> cells, double y, Typeface face, double size, Brush brush, double numbersLeft, double[] rights)
    {
        Draw(dc, cells[0], face, size, brush, Padding, y + 4, ColumnRight[0] - Padding, TextAlignment.Right);
        Draw(dc, cells[1], face, size, brush, 62, y + 4, numbersLeft - 62 - 4, TextAlignment.Left);

        double left = numbersLeft;
        for (int i = 2; i < cells.Count && i - 2 < rights.Length; i++)
        {
            double right = rights[i - 2];
            Draw(dc, cells[i], face, size, brush, left, y + 4, right - left, TextAlignment.Right);
            left = right;
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
