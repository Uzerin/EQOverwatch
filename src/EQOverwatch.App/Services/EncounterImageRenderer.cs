using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EQOverwatch.Core.Export;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Theming;

namespace EQOverwatch.App.Services;

/// <summary>
/// Draws the "Copy Image for Discord" picture in three columns under the encounter summary and graph: the damage dealt (Total, SDPS,
/// Max Hit, Max Crit, Crit %) with every attacker, then healing, then tanking (damage taken), and the damage taken over time across the bottom.
/// Every row is listed, the product footer runs along the bottom, and everything uses the active theme's colors. Rendered at twice the layout size so it
/// stays sharp when Discord scales it. Call on the UI thread.
/// </summary>
public static class EncounterImageRenderer
{
    private const double Scale = 2.0;
    private const double MinContentWidth = 540;
    private const double MinSideWidth = 300;
    private const double MaxNameWidth = 280;
    private const double ColumnGap = 16;
    private const double Padding = 22;
    private const double RowHeight = 28;
    private const double HeaderHeight = 74;

    /// <summary>The estimated regen and hit points line under the summary (only when there is an estimate).</summary>
    private const double RegenLineHeight = 19;

    private const string RegenColor = "#6FDC8C";
    private const double TableHeaderHeight = 26;
    private const double FooterHeight = 38;
    private const double SectionHeight = 40;
    private const double SectionGap = 10;
    private const double TitleHeight = SectionHeight - SectionGap;
    private const double GraphPlotHeight = 170;
    private const double IncomingPlotHeight = 110;

    private static readonly Typeface Regular = new("Segoe UI");
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// <summary>The picture as PNG bytes (for posting to Discord). Call on the UI thread.</summary>
    public static byte[] RenderPng(EncounterSnapshot encounter, ThemePalette palette, string footer, int topCount = EncounterExport.AllAttackers, AttackColorScheme? colors = null)
    {
        BitmapSource image = Render(encounter, palette, footer, topCount, colors);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new System.IO.MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static BitmapSource Render(EncounterSnapshot encounter, ThemePalette palette, string footer, int topCount = EncounterExport.AllAttackers, AttackColorScheme? colors = null)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        ArgumentNullException.ThrowIfNull(palette);

        IReadOnlyList<EncounterExportRow> rows = EncounterExport.BuildRows(encounter, topCount);
        IReadOnlyList<DamageTakenSnapshot> taken = encounter.TakenRows;
        IReadOnlyList<HealingSnapshot> healing = encounter.HealingRows;

        colors ??= AttackColorScheme.Current;
        IReadOnlyList<AttackType> legend = AttackTypes.All
            .Where(t => rows.Any(r => r.Attacks is { } a && a.Any(x => x.Type == t && x.Total > 0)))
            .ToList();

        // ── the tables, with every column as wide as its widest cell ──
        var dpsTable = new Table(
            new[] { "#", "Name", "Class", "Total", "Hits", "Misses", "Hit Rate", "SDPS", "Max Hit", "Max Crit", "Crit %" },
            new[] { false, false, false, true, true, true, true, true, true, true, true }, flexColumn: 1,
            rows.Select(r => new[]
            {
                r.Rank.ToString(CultureInfo.InvariantCulture), r.DisplayName, r.Class, r.Total, r.Hits, r.Misses, r.HitRate, r.Sdps, r.MaxHit, r.MaxCrit, r.CritPercent,
            }).ToList());

        long healTotal = healing.Sum(h => h.Effective);
        Table? takenTable = taken.Count == 0 ? null : new Table(
            new[] { "#", "Name", "Taken", "% of total", "Hits", "Avoid %", "Max Hit" },
            new[] { false, false, true, true, true, true, true }, flexColumn: 1,
            taken.Select((t, i) => new[]
            {
                (i + 1).ToString(CultureInfo.InvariantCulture), t.DisplayName, t.TotalDamage.ToString("N0", CultureInfo.InvariantCulture),
                t.PercentOfTotal.ToString("0.0", CultureInfo.InvariantCulture) + "%", t.Hits.ToString("N0", CultureInfo.InvariantCulture),
                t.Attacks > 0 ? t.AvoidPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%" : "-",
                t.MaxHit.ToString("N0", CultureInfo.InvariantCulture),
            }).ToList());

        Table? healTable = healing.Count == 0 ? null : new Table(
            new[] { "#", "Name", "HPS", "Total", "Effective Heals", "Overheals", "Casts", "Biggest" },
            new[] { false, false, true, true, true, true, true, true }, flexColumn: 1,
            healing.Select((h, i) => new[]
            {
                (i + 1).ToString(CultureInfo.InvariantCulture), h.Name,
                Math.Round(h.Hps).ToString("N0", CultureInfo.InvariantCulture),
                (h.Effective + h.Overheal).ToString("N0", CultureInfo.InvariantCulture),
                h.Effective.ToString("N0", CultureInfo.InvariantCulture),
                h.Overheal.ToString("N0", CultureInfo.InvariantCulture) + " (" + h.OverhealPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%)",
                h.Heals.ToString("N0", CultureInfo.InvariantCulture),
                h.MaxHeal.ToString("N0", CultureInfo.InvariantCulture),
            }).ToList());

        string dpsTotalText = $"{encounter.TotalDamage.ToString("N0", CultureInfo.InvariantCulture)} total";
        const string takenTitle = "Tanking";
        string takenTotalText = $"{encounter.TotalDamageTaken.ToString("N0", CultureInfo.InvariantCulture)} total";
        string healTitle = healing.Any(h => h.CritHeals > 0) ? "Healing  (lighter shade = crits)" : "Healing";
        string healTotalText = $"{healTotal.ToString("N0", CultureInfo.InvariantCulture)} total";

        // Three columns side by side (DPS, Healing, Tanking; deaths show there and on the graphs), each as wide as its widest table or title needs.
        double dpsContent = Math.Max(MinContentWidth, Math.Max(dpsTable.NaturalWidth, TitleWidth("DPS", dpsTotalText)));
        double healContent = Math.Max(MinSideWidth, Math.Max(healTable?.NaturalWidth ?? 0, TitleWidth(healTitle, healTotalText)));
        double tankContent = Math.Max(MinSideWidth, new[]
        {
            takenTable?.NaturalWidth ?? 0, TitleWidth(takenTitle, takenTotalText),
        }.Max());

        dpsTable.FitTo(dpsContent);
        healTable?.FitTo(healContent);
        takenTable?.FitTo(tankContent);

        double dpsColumn = dpsContent + (2 * Padding);
        double healColumn = healContent + (2 * Padding);
        double tankColumn = tankContent + (2 * Padding);
        double width = dpsColumn + healColumn + tankColumn;

        int legendPerLine = Math.Max(1, (int)Math.Floor(dpsContent / LegendEntryWidth));
        double legendHeight = legend.Count == 0 ? 0 : LegendCaptionHeight + (Math.Ceiling(legend.Count / (double)legendPerLine) * LegendLineHeight) + 6;

        static double SectionBlock(int rowCount) => TitleHeight + (rowCount == 0 ? RowHeight : TableHeaderHeight + (rowCount * RowHeight));

        double dpsHeight = TitleHeight + TableHeaderHeight + (Math.Max(1, rows.Count) * RowHeight) + legendHeight;
        double healHeight = SectionBlock(healing.Count);
        double tankHeight = SectionBlock(taken.Count);

        // The graph runs the full width of the picture, between the mob's name and the tables.
        DpsTimeline? timeline = encounter.Timeline;
        double graphWidth = width - (2 * Padding);
        double graphHeight = timeline is null ? 0 : DpsGraph.TotalHeight(timeline, graphWidth, GraphPlotHeight, Scale);
        double graphBlock = timeline is null ? 0 : TitleHeight + graphHeight + SectionGap;
        double headerHeight = HeaderHeight + (encounter.RegenText.Length > 0 ? RegenLineHeight : 0);
        double bodyTop = headerHeight + graphBlock;
        double columnsHeight = Math.Max(dpsHeight, Math.Max(healHeight, tankHeight));

        // The damage the raid took per second runs the full width under the tables.
        bool incoming = timeline?.Incoming is { Count: > 0 };
        double incomingBlock = incoming ? SectionGap + TitleHeight + DpsGraph.IncomingHeight(IncomingPlotHeight) : 0;
        double height = bodyTop + columnsHeight + incomingBlock + FooterHeight;

        Brush window = Solid(palette.WindowBg);
        Brush card = Solid(palette.CardBg);
        Brush accent = Solid(palette.Accent);
        Brush text = Solid(palette.TextPrimary);
        Brush muted = MixBrush(palette.TextPrimary, palette.WindowBg, 0.45);
        Brush barFill = WithOpacity(palette.Accent, 0.38);
        Brush takenFill = WithOpacity(TakenColor, 0.38);
        Brush line = Solid(palette.TextPrimary, 0x30);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window, null, new Rect(0, 0, width, height));
            dc.DrawRoundedRectangle(card, null, new Rect(10, 10, width - 20, height - 20), 12, 12);
            dc.DrawRoundedRectangle(accent, null, new Rect(10, 10, 6, height - 20), 3, 3);

            // header, across all three columns
            Draw(dc, encounter.TargetName, Bold, 22, text, Padding + 6, 20, width - (2 * Padding) - 6, TextAlignment.Left);
            Draw(dc, SummaryLine(encounter), Regular, 13, muted, Padding + 6, 52, width - (2 * Padding) - 6, TextAlignment.Left);
            if (encounter.RegenText.Length > 0)
            {
                Draw(dc, encounter.RegenText, Regular, 13, Solid(RegenColor), Padding + 6, 52 + RegenLineHeight, width - (2 * Padding) - 6, TextAlignment.Left);
            }

            if (timeline is not null)
            {
                DrawTitle(dc, "DPS over time", "group + top " + timeline.Players.Count.ToString(CultureInfo.InvariantCulture), graphWidth, headerHeight, text, muted, Bold);
                DpsGraph.Draw(dc, timeline, new Rect(Padding, headerHeight + TitleHeight, graphWidth, graphHeight), GraphPlotHeight, text, muted, line, text, Scale);
            }

            // thin dividers between the columns
            Brush divider = Solid(palette.TextPrimary, 0x20);
            double dividerHeight = Math.Max(0, columnsHeight - 4);
            dc.DrawRectangle(divider, null, new Rect(dpsColumn - 0.5, bodyTop + 4, 1, dividerHeight));
            dc.DrawRectangle(divider, null, new Rect(dpsColumn + healColumn - 0.5, bodyTop + 4, 1, dividerHeight));

            // ── first column: damage dealt ──
            double y = bodyTop;
            DrawTitle(dc, "DPS", dpsTotalText, dpsContent, y, text, muted, Bold);
            y += SectionHeight - SectionGap;

            dpsTable.DrawHeader(dc, y, muted);
            y += TableHeaderHeight;
            dc.DrawRectangle(line, null, new Rect(Padding, y - 2, dpsContent, 1));

            for (int i = 0; i < rows.Count; i++)
            {
                EncounterExportRow row = rows[i];
                double barWidth = dpsContent * Math.Clamp(row.Fill, 0, 1);
                if (barWidth > 0)
                {
                    DrawDamageBar(dc, new Rect(Padding, y + 2, barWidth, RowHeight - 4), row.Attacks, colors, barFill);
                }

                dpsTable.DrawRow(dc, i, y, text);
                y += RowHeight;
            }

            if (rows.Count == 0)
            {
                Draw(dc, "No attackers recorded.", Regular, 14, muted, Padding + 6, y + 4, 400, TextAlignment.Left);
            }

            if (legend.Count > 0)
            {
                y += 4;
                Draw(dc, "Bar colors: each attack type's color is for regular hits, the lighter shade is crits.", Regular, 12, muted,
                    Padding + 6, y, dpsContent, TextAlignment.Left);
                y += LegendCaptionHeight;
                for (int i = 0; i < legend.Count; i++)
                {
                    double x = Padding + 6 + ((i % legendPerLine) * LegendEntryWidth);
                    double ly = y + ((i / legendPerLine) * LegendLineHeight);
                    dc.DrawRoundedRectangle(Solid(colors.Normal(legend[i])), null, new Rect(x, ly + 4, 12, 12), 3, 3);
                    dc.DrawRoundedRectangle(Solid(colors.Crit(legend[i])), null, new Rect(x + 14, ly + 4, 12, 12), 3, 3);
                    Draw(dc, AttackTypes.Label(legend[i]), Regular, 12, text, x + 32, ly + 2, LegendEntryWidth - 36, TextAlignment.Left);
                }
            }

            // ── second column: healing ──
            dc.PushTransform(new TranslateTransform(dpsColumn, 0));
            double hy = bodyTop;
            DrawTitle(dc, healTitle, healTotalText, healContent, hy, text, muted, Bold);
            hy += SectionHeight - SectionGap;
            if (healTable is null)
            {
                Draw(dc, "No heals.", Regular, 14, muted, Padding + 6, hy + 4, 400, TextAlignment.Left);
            }
            else
            {
                healTable.DrawHeader(dc, hy, muted);
                hy += TableHeaderHeight;
                dc.DrawRectangle(line, null, new Rect(Padding, hy - 2, healContent, 1));

                long topHeal = healing.Max(h => h.Effective);
                for (int i = 0; i < healing.Count; i++)
                {
                    HealingSnapshot h = healing[i];
                    double barWidth = topHeal > 0 ? healContent * ((double)h.Effective / topHeal) : 0;
                    if (barWidth > 0)
                    {
                        DrawSegments(dc, new Rect(Padding, hy + 2, barWidth, RowHeight - 4),
                            new[] { (h.Effective - h.CritEffective, colors.Healing), (h.CritEffective, colors.HealingCrit) });
                    }

                    healTable.DrawRow(dc, i, hy, text);
                    hy += RowHeight;
                }
            }

            dc.Pop();

            // ── third column: tanking ──
            dc.PushTransform(new TranslateTransform(dpsColumn + healColumn, 0));
            double ry = bodyTop;
            DrawTitle(dc, takenTitle, takenTotalText, tankContent, ry, text, muted, Bold);
            ry += SectionHeight - SectionGap;
            if (takenTable is null)
            {
                Draw(dc, "No damage taken.", Regular, 14, muted, Padding + 6, ry + 4, 400, TextAlignment.Left);
                ry += RowHeight;
            }
            else
            {
                takenTable.DrawHeader(dc, ry, muted);
                ry += TableHeaderHeight;
                dc.DrawRectangle(line, null, new Rect(Padding, ry - 2, tankContent, 1));

                long top = taken.Max(t => t.TotalDamage);
                for (int i = 0; i < taken.Count; i++)
                {
                    double barWidth = top > 0 ? tankContent * ((double)taken[i].TotalDamage / top) : 0;
                    if (barWidth > 0)
                    {
                        dc.DrawRoundedRectangle(takenFill, null, new Rect(Padding, ry + 2, barWidth, RowHeight - 4), 5, 5);
                    }

                    takenTable.DrawRow(dc, i, ry, text);
                    ry += RowHeight;
                }
            }

            dc.Pop();

            if (incoming)
            {
                double iy = bodyTop + columnsHeight + SectionGap;
                long takenSum = encounter.TotalDamageTaken;
                DrawTitle(dc, "Damage taken over time", $"{takenSum.ToString("N0", CultureInfo.InvariantCulture)} total", graphWidth, iy, text, muted, Bold);
                DpsGraph.DrawIncoming(dc, timeline!, new Rect(Padding, iy + TitleHeight, graphWidth, DpsGraph.IncomingHeight(IncomingPlotHeight)),
                    IncomingPlotHeight, muted, line, Scale);
            }

            // footer
            Draw(dc, footer, Regular, 12, muted, Padding, height - FooterHeight + 4, width - (2 * Padding), TextAlignment.Right);
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * Scale), (int)Math.Ceiling(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static double MeasureText(string text, Typeface face, double size) =>
        new FormattedText(text ?? string.Empty, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Brushes.Black, Scale)
            .WidthIncludingTrailingWhitespace;

    /// <summary>The room a section's title and its "N total" text need side by side.</summary>
    private static double TitleWidth(string title, string total) =>
        MeasureText(title, Bold, 16) + MeasureText(total, Regular, 13) + 36;

    private static void DrawTitle(DrawingContext dc, string title, string total, double contentWidth, double y, Brush text, Brush muted, Typeface bold)
    {
        Draw(dc, title, bold, 16, text, Padding + 6, y, contentWidth, TextAlignment.Left);
        Draw(dc, total, Regular, 13, muted, Padding + contentWidth - 220, y + 2, 220, TextAlignment.Right);
    }

    /// <summary>
    /// A table whose columns are each as wide as the widest header or cell in them. Extra room (when its half is wider than the table)
    /// goes to one flexible column. A column's text is right-aligned for numbers.
    /// </summary>
    private sealed class Table
    {
        private readonly string[] _headers;
        private readonly bool[] _rightAligned;
        private readonly int _flex;
        private readonly IReadOnlyList<string[]> _rows;
        private readonly double[] _widths;
        private double[] _lefts = Array.Empty<double>();

        public Table(string[] headers, bool[] rightAligned, int flexColumn, IReadOnlyList<string[]> rows)
        {
            _headers = headers;
            _rightAligned = rightAligned;
            _flex = flexColumn;
            _rows = rows;
            _widths = new double[headers.Length];
            for (int c = 0; c < headers.Length; c++)
            {
                double widest = MeasureText(headers[c], Bold, 12);
                foreach (string[] row in rows)
                {
                    widest = Math.Max(widest, MeasureText(row[c], Regular, 14));
                }

                // The rank column is right-aligned and a name can be long; names are cut with "..." past MaxNameWidth.
                _widths[c] = Math.Ceiling(c == flexColumn && c == 1 ? Math.Min(widest, MaxNameWidth) : widest) + 2;
            }

            Layout();
        }

        public double NaturalWidth => _widths.Sum() + (ColumnGap * (_widths.Length - 1));

        /// <summary>Makes the table exactly <paramref name="contentWidth"/> wide by growing its flexible column.</summary>
        public void FitTo(double contentWidth)
        {
            double extra = contentWidth - NaturalWidth;
            if (extra > 0)
            {
                _widths[_flex] += extra;
            }

            Layout();
        }

        private void Layout()
        {
            _lefts = new double[_widths.Length];
            double x = Padding;
            for (int c = 0; c < _widths.Length; c++)
            {
                _lefts[c] = x;
                x += _widths[c] + ColumnGap;
            }
        }

        public void DrawHeader(DrawingContext dc, double y, Brush brush) => DrawCells(dc, _headers, y, Bold, 12, brush);

        public void DrawRow(DrawingContext dc, int index, double y, Brush brush) => DrawCells(dc, _rows[index], y, Regular, 14, brush);

        private void DrawCells(DrawingContext dc, string[] cells, double y, Typeface face, double size, Brush brush)
        {
            for (int c = 0; c < cells.Length; c++)
            {
                bool right = _rightAligned[c] || c == 0;
                Draw(dc, cells[c], face, size, brush, _lefts[c], y + 4, _widths[c], right ? TextAlignment.Right : TextAlignment.Left);
            }
        }
    }


    private const double LegendCaptionHeight = 22;
    private const double LegendLineHeight = 22;
    private const int LegendPerLine = 5;
    private const double LegendEntryWidth = 136;

    /// <summary>
    /// A player's damage bar. With a per-attack breakdown it is cut into segments, a regular and a lighter crit shade for each attack
    /// type, widths in proportion to the damage; without one it is the plain accent bar.
    /// </summary>
    private static void DrawDamageBar(DrawingContext dc, Rect bar, IReadOnlyList<AttackSlice>? attacks, AttackColorScheme colors, Brush plain)
    {
        long total = attacks?.Sum(a => a.Total) ?? 0;
        if (attacks is null || total <= 0)
        {
            dc.DrawRoundedRectangle(plain, null, bar, 5, 5);
            return;
        }

        var parts = new List<(long Damage, string Hex)>();
        foreach (AttackSlice slice in attacks.OrderBy(a => a.Type))
        {
            parts.Add((slice.Normal, colors.Normal(slice.Type)));
            parts.Add((slice.Crit, colors.Crit(slice.Type)));
        }

        DrawSegments(dc, bar, parts);
    }

    /// <summary>A rounded bar cut into colored segments whose widths follow the amounts (empty ones are skipped).</summary>
    private static void DrawSegments(DrawingContext dc, Rect bar, IReadOnlyList<(long Amount, string Hex)> parts)
    {
        long total = parts.Sum(p => Math.Max(0, p.Amount));
        if (total <= 0)
        {
            return;
        }

        dc.PushClip(new RectangleGeometry(bar, 5, 5));
        double x = bar.X;
        foreach ((long amount, string hex) in parts)
        {
            if (amount <= 0)
            {
                continue;
            }

            double width = bar.Width * amount / total;
            dc.DrawRectangle(Solid(hex, 0xB0), null, new Rect(x, bar.Y, width + 0.5, bar.Height));
            x += width;
        }

        dc.Pop();
    }

    /// <summary>The picture of a made-up fight in the given colors, for the Colors window's preview.</summary>
    public static BitmapSource RenderPreview(AttackColorScheme colors, ThemePalette palette, string footer)
    {
        static ParticipantSnapshot Player(string name, string cls, params (AttackType Type, long Normal, long Crit)[] attacks)
        {
            AttackSlice[] slices = attacks.Select(a => new AttackSlice(a.Type, a.Normal, a.Crit)).ToArray();
            long total = slices.Sum(a => a.Total);
            return new ParticipantSnapshot(
                name, total, 0, total / 200.0, total / 200.0, 9000, 400, 20, 0.95, 14000, 60, 15.0, cls, 0, 8000, slices);
        }

        ParticipantSnapshot[] players =
        {
            Player("Stackprime", "BER", (AttackType.Slash, 610000, 240000), (AttackType.Frenzy, 180000, 90000), (AttackType.Kick, 40000, 12000)),
            Player("Ream", "ROG", (AttackType.Backstab, 350000, 230000), (AttackType.Pierce, 300000, 70000), (AttackType.Hit, 20000, 0)),
            Player("Oodles", "MNK", (AttackType.HandToHand, 420000, 160000), (AttackType.Kick, 210000, 95000), (AttackType.Hit, 30000, 0)),
            Player("Trickster", "RNG", (AttackType.Archery, 380000, 210000), (AttackType.Slash, 120000, 30000), (AttackType.Pet, 90000, 20000)),
            Player("Wuzzle", "MAG", (AttackType.Spell, 420000, 100000), (AttackType.Pet, 260000, 70000)),
            Player("Gruk", "WAR", (AttackType.Crush, 240000, 60000), (AttackType.Bash, 50000, 10000), (AttackType.OtherMelee, 25000, 0), (AttackType.DamageShield, 38000, 0)),
        };

        long sum = players.Sum(p => p.TotalDamage);
        ParticipantSnapshot[] participants = players
            .Select(p => p with { PercentOfGroupDamage = 100.0 * p.TotalDamage / sum })
            .OrderByDescending(p => p.TotalDamage)
            .ToArray();

        HealingSnapshot[] healing =
        {
            new("Pallas", 201000, 40000, 16.6, 120, 5200, 985, "Complete Heal", 70000, 14),
            new("Brell", 100500, 30000, 23.0, 200, 1800, 493, "Celestial Elixir", 12000, 6),
        };

        DamageTakenSnapshot[] taken =
        {
            new("Gruk", 61204, 70.0, 210, 1820, 0, 150, 60, 40, 30, 10, 10),
            new("Stackprime", 26100, 30.0, 70, 1544, 1, 20, 10, 5, 0, 5, 0),
        };

        DateTime start = new(2026, 1, 1, 21, 0, 0);
        DeathSnapshot[] deaths = { new(start.AddSeconds(134), "Stackprime", "Sample fight", "1,544 punch") };
        var encounter = new EncounterSnapshot(
            0, "Sample fight", start, start.AddSeconds(204), 204, sum, false, participants, taken, null, healing, deaths, true);
        return Render(encounter, palette, footer, EncounterExport.AllAttackers, colors);
    }


    private static string SummaryLine(EncounterSnapshot encounter) =>
        $"{encounter.StartTime:yyyy-MM-dd HH:mm:ss}   •   {EncounterExport.FormatDuration(encounter.DurationSeconds)}   •   " +
        $"{encounter.TotalDamage.ToString("N0", CultureInfo.InvariantCulture)} damage   •   " +
        $"group DPS {Math.Round(encounter.TotalDamage / Math.Max(1.0, encounter.DurationSeconds)).ToString("N0", CultureInfo.InvariantCulture)}";

    /// <summary>Warm color for the damage-taken bars, so they read apart from the damage-dealt bars.</summary>
    private const string TakenColor = "#E5845C";

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
