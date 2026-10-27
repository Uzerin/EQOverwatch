using System.Globalization;
using System.Text;
using EQCompanion.Core.Models;

namespace EQCompanion.Core.Spells;

public enum SpellFileFormat
{
    /// <summary>One file: cast and fade messages are columns 6, 7 and 8 of <c>spells_us.txt</c>.</summary>
    Legacy,

    /// <summary>Daybreak split layout: columns shifted left by 5, messages live in <c>spells_us_str.txt</c>.</summary>
    Split,
}

/// <summary>Where the messages sit in <c>spells_us_str.txt</c> (column 0 is always the spell id).</summary>
public sealed record SpellStringsLayout(int CastOnYou = 1, int CastOnOther = 2, int SpellFades = 3);

public sealed record SpellLoadReport(
    string FilePath,
    string? StringsFilePath,
    SpellFileFormat Format,
    int CasterLevel,
    int RowsRead,
    int RowsKept,
    int RowsSkippedNoDuration,
    int RowsMalformed,
    string? Warning,
    int DefinitionCount = 0);

/// <summary>
/// Loads <c>spells_us.txt</c> (and the companion <c>spells_us_str.txt</c> when present) into an in-memory
/// <see cref="SpellDatabase"/>.
/// <para>
/// <b>Layouts.</b> Legacy: 0 id, 1 name, 6 cast-on-you, 7 cast-on-other, 8 fades, 13 cast time (ms), 16 duration formula,
/// 17 max ticks, 83 GoodEffect, 115 Bard level. Split: 0 id, 1 name, 8 cast time, 11 formula, 12 max ticks, 28 GoodEffect,
/// 43 Bard level (class levels are columns 36-51 in class order, Warrior first); the three messages come from <c>spells_us_str.txt</c> keyed by id (<c>#</c> lines skipped). Their columns are read from
/// that file's header (CASTEDMETXT, CASTEDOTHERTXT, SPELLGONE; normally 3, 4, 5), or from an explicit <see cref="SpellStringsLayout"/>. The split layout is chosen when the companion file exists or when column 6 of a data
/// row is an integer (a legacy row has text or nothing there).
/// </para>
/// <para>
/// <b>Duration spells</b> (<see cref="SpellDatabase.Spells"/>) get a base duration computed once per row from the caster level
/// and the row's formula (ticks x 6 seconds, no focus or AA math). Rows that come out at zero seconds, and formula 50, are
/// discarded from that list. <b>Every</b> spell with a landing message, instant or not, is kept in
/// <see cref="SpellDatabase.AllSpellDefinitions"/> for the raid-boss picker.
/// </para>
/// <para>
/// <b>Categories.</b> A spell with a Bard class level (1-254) is a song; otherwise GoodEffect 0 is a debuff and anything else a
/// buff. A row too short to contain those columns is treated as a buff.
/// </para>
/// </summary>
public sealed class SpellDatabaseService
{
    public const int DefaultCasterLevel = 60;
    public const int MinCasterLevel = 1;
    public const int MaxCasterLevel = 125;

    /// <summary>Songs last this long or less (6 ticks x 4 = 24 s); used to recognise a song cast with "sing".</summary>
    public const int MaxBardSongSeconds = 24;

    private const int LegacyCastOnYou = 6;
    private const int LegacyCastOnOther = 7;
    private const int LegacySpellFades = 8;
    private const int LegacyCastTime = 13;
    private const int LegacyFormula = 16;
    private const int LegacyMaxTicks = 17;
    private const int LegacyGoodEffect = 83;
    private const int LegacyBardLevel = 115;

    private const int SplitCastTime = 8;
    private const int SplitFormula = 11;
    private const int SplitMaxTicks = 12;
    private const int SplitGoodEffect = 28;
    private const int SplitBardLevel = 43;
    private const int SplitRecastMs = 10;

    /// <summary>Spell rows recast more slowly than this are offered on the AAs &amp; Discs tab.</summary>
    public const int MinAbilityRecastSeconds = 10;

    private const int DetectionColumn = 6;
    private const int DetectionSampleRows = 200;
    private const int SecondsPerTick = 6;
    private const int PermanentFormula = 50;
    private const int NoBardLevel = 255; // the files use 255 for "this class cannot cast it"

    private readonly SpellStringsLayout? _explicitStringsLayout;
    private SpellDatabase _database = SpellDatabase.Empty;

    public SpellDatabaseService(SpellStringsLayout? stringsLayout = null)
    {
        _explicitStringsLayout = stringsLayout;
    }

    /// <summary>The most recently loaded database (empty until <see cref="Load"/> succeeds). Safe to read from any thread.</summary>
    public SpellDatabase Database => Volatile.Read(ref _database);

    /// <summary>Raised on the loading thread after <see cref="Database"/> is replaced by <see cref="Load"/> or <see cref="Clear"/>.</summary>
    public event Action? DatabaseChanged;

    /// <summary>Parses the file and swaps in the new database. Throws if the file is missing or unreadable.</summary>
    public SpellLoadReport Load(string path, int casterLevel = DefaultCasterLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (casterLevel is < MinCasterLevel or > MaxCasterLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(casterLevel), $"Caster level must be {MinCasterLevel}-{MaxCasterLevel}.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Spell file not found.", path);
        }

        string fullPath = Path.GetFullPath(path);
        string stringsPath = Path.Combine(
            Path.GetDirectoryName(fullPath)!,
            Path.GetFileNameWithoutExtension(fullPath) + "_str" + Path.GetExtension(fullPath));
        bool stringsExist = File.Exists(stringsPath);

        SpellFileFormat format = stringsExist || LooksSplit(fullPath) ? SpellFileFormat.Split : SpellFileFormat.Legacy;
        bool split = format == SpellFileFormat.Split;

        int castTimeColumn = split ? SplitCastTime : LegacyCastTime;
        int formulaColumn = split ? SplitFormula : LegacyFormula;
        int ticksColumn = split ? SplitMaxTicks : LegacyMaxTicks;
        int goodEffectColumn = split ? SplitGoodEffect : LegacyGoodEffect;
        int bardColumn = split ? SplitBardLevel : LegacyBardLevel;
        int requiredColumn = split ? SplitMaxTicks : Math.Max(LegacyMaxTicks, LegacySpellFades);

        var columns = new ColumnReader(Math.Max(goodEffectColumn, bardColumn));
        var recasts = new Dictionary<int, (string Name, int Seconds)>();
        var kept = new Dictionary<int, SpellInfo>();
        var definitions = new Dictionary<int, SpellDefinition>();
        int rowsRead = 0, skipped = 0, malformed = 0;

        using (var reader = new StreamReader(fullPath, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                rowsRead++;
                columns.Read(line);

                if (columns.Count <= requiredColumn
                    || !columns.TryInt(0, out int id)
                    || !columns.TryInt(castTimeColumn, out int castTimeMs)
                    || !columns.TryInt(formulaColumn, out int formula)
                    || !columns.TryInt(ticksColumn, out int maxTicks))
                {
                    malformed++;
                    continue;
                }

                string name = columns.Get(1);
                string castOnYou = split ? string.Empty : columns.Get(LegacyCastOnYou);
                string castOnOther = split ? string.Empty : columns.Get(LegacyCastOnOther);
                string fades = split ? string.Empty : columns.Get(LegacySpellFades);

                // Every readable row is a candidate for the raid boss picker; the split layout fills the messages in later.
                definitions[id] = new SpellDefinition(id, name, castOnYou, castOnOther);

                if (split && name.Length > 0 && columns.TryInt(SplitRecastMs, out int recastMs) && recastMs >= MinAbilityRecastSeconds * 1000)
                {
                    recasts[id] = (name, recastMs / 1000);
                }

                if (formula == PermanentFormula)
                {
                    skipped++;
                    continue;
                }

                int ticks = ComputeBaseTicks(formula, casterLevel, maxTicks);
                if (ticks <= 0)
                {
                    skipped++;
                    continue;
                }

                int seconds = (int)Math.Min((long)ticks * SecondsPerTick, int.MaxValue);
                TimerOverlayCategory category = Categorize(
                    columns.TryInt(bardColumn, out int bardLevel) ? bardLevel : null,
                    columns.TryInt(goodEffectColumn, out int goodEffect) ? goodEffect : null);

                kept[id] = new SpellInfo(id, name, castOnYou, castOnOther, fades, castTimeMs, formula, maxTicks, seconds, category);
            }
        }

        string? warning = null;
        if (split && stringsExist)
        {
            ApplyStrings(stringsPath, kept, definitions);
        }
        else if (split)
        {
            warning = $"{Path.GetFileName(stringsPath)} was not found next to the spell file, so landing and fade messages are unavailable " +
                      "and tracked spells cannot start or cancel timers.";
        }

        IEnumerable<AbilityInfo> abilities = recasts.Select(r =>
            new AbilityInfo(r.Key, r.Value.Name, r.Value.Seconds, kept.TryGetValue(r.Key, out SpellInfo? lasting) ? lasting.BaseDurationSeconds : 0));
        var database = new SpellDatabase(kept.Values, casterLevel, definitions.Values, abilities);
        Volatile.Write(ref _database, database);
        DatabaseChanged?.Invoke();

        return new SpellLoadReport(
            fullPath, split && stringsExist ? stringsPath : null, format, casterLevel,
            rowsRead, database.Count, skipped, malformed, warning, database.AllSpellDefinitions.Count);
    }

    /// <summary>Drops the loaded database.</summary>
    public void Clear()
    {
        Volatile.Write(ref _database, SpellDatabase.Empty);
        DatabaseChanged?.Invoke();
    }

    /// <summary>
    /// Duration in ticks (6 seconds each) at <paramref name="level"/>, before focus and AA effects.
    /// A result of zero or less means the spell has no usable duration.
    /// </summary>
    public static int ComputeBaseTicks(int formula, int level, int maxTicks) => formula switch
    {
        0 => 0,
        1 => Math.Min((int)Math.Ceiling(level / 2.0), maxTicks),
        2 => Math.Min(level <= 3 ? 1 : (int)Math.Ceiling(level / 2.0) + 5, maxTicks),
        3 => Math.Min(level * 30, maxTicks),
        4 => maxTicks == 0 ? 50 : maxTicks,
        5 => Math.Min(2, maxTicks == 0 ? 2 : maxTicks),
        6 => Math.Min(level / 2 + 2, maxTicks),
        7 => maxTicks > 0 ? Math.Min(level, maxTicks) : level,
        8 => Math.Min(level + 10, maxTicks),
        9 => Math.Min(level * 2 + 10, maxTicks),
        10 => Math.Min(level * 3 + 10, maxTicks),
        11 or 12 => maxTicks,
        _ => maxTicks,
    };

    /// <summary>
    /// Songs: a Bard class level of 1-254. Otherwise detrimental (GoodEffect 0) spells are debuffs and everything else,
    /// including rows without those columns, is a buff.
    /// </summary>
    public static TimerOverlayCategory Categorize(int? bardLevel, int? goodEffect)
    {
        if (bardLevel is >= 1 and < NoBardLevel)
        {
            return TimerOverlayCategory.Songs;
        }

        return goodEffect == 0 ? TimerOverlayCategory.Debuffs : TimerOverlayCategory.Buffs;
    }

    private void ApplyStrings(string stringsPath, Dictionary<int, SpellInfo> kept, Dictionary<int, SpellDefinition> definitions)
    {
        // An explicit layout wins; otherwise the file's own "#SPELLINDEX^CASTERMETXT^..." header says where the messages are.
        SpellStringsLayout layout = _explicitStringsLayout ?? ReadStringsHeader(stringsPath) ?? new SpellStringsLayout();
        int needed = Math.Max(layout.CastOnYou, Math.Max(layout.CastOnOther, layout.SpellFades));
        var columns = new ColumnReader(needed);

        using var reader = new StreamReader(stringsPath, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            columns.Read(line);
            if (columns.Count <= needed || !columns.TryInt(0, out int id) || !definitions.TryGetValue(id, out SpellDefinition? definition))
            {
                continue;
            }

            string castOnYou = columns.Get(layout.CastOnYou);
            string castOnOther = columns.Get(layout.CastOnOther);
            definitions[id] = definition with { CastOnYou = castOnYou, CastOnOther = castOnOther };

            if (kept.TryGetValue(id, out SpellInfo? spell))
            {
                kept[id] = spell with
                {
                    CastOnYou = castOnYou,
                    CastOnOther = castOnOther,
                    SpellFades = columns.Get(layout.SpellFades),
                };
            }
        }
    }

    /// <summary>
    /// Reads the header of <c>spells_us_str.txt</c>
    /// (<c>#SPELLINDEX^CASTERMETXT^CASTEROTHERTXT^CASTEDMETXT^CASTEDOTHERTXT^SPELLGONE</c>). "Cast on you" is CASTEDMETXT (what you
    /// see when the spell lands on you), "cast on other" is CASTEDOTHERTXT and the fade message is SPELLGONE. CASTERMETXT and
    /// CASTEROTHERTXT are caster-side texts and are not used. Returns null when the header is missing or lacks those names.
    /// </summary>
    internal static SpellStringsLayout? ReadStringsHeader(string path)
    {
        using var reader = new StreamReader(path, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 12);
        for (int i = 0; i < 5 && reader.ReadLine() is { } line; i++)
        {
            if (line.Length == 0 || line[0] != '#')
            {
                continue;
            }

            string[] names = line.TrimStart('#').Split('^');
            int you = Array.FindIndex(names, n => n.Trim().Equals("CASTEDMETXT", StringComparison.OrdinalIgnoreCase));
            int other = Array.FindIndex(names, n => n.Trim().Equals("CASTEDOTHERTXT", StringComparison.OrdinalIgnoreCase));
            int gone = Array.FindIndex(names, n => n.Trim().Equals("SPELLGONE", StringComparison.OrdinalIgnoreCase));
            if (you > 0 && other > 0 && gone > 0)
            {
                return new SpellStringsLayout(you, other, gone);
            }
        }

        return null;
    }

    /// <summary>A split-layout row has a number in column 6; a legacy row has the cast-on-you text (or nothing) there.</summary>
    private static bool LooksSplit(string path)
    {
        var columns = new ColumnReader(DetectionColumn);
        using var reader = new StreamReader(path, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
        int sampled = 0;
        string? line;
        while (sampled < DetectionSampleRows && (line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            sampled++;
            columns.Read(line);
            if (columns.Count > DetectionColumn && columns.TryInt(DetectionColumn, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Locates the first <c>maxColumn + 1</c> caret-delimited columns of a line without splitting the rest of it (rows have
    /// more than 200 columns) and only materializes the strings that are asked for.
    /// </summary>
    private sealed class ColumnReader
    {
        private readonly int[] _starts;
        private readonly int[] _lengths;
        private string _line = string.Empty;

        public ColumnReader(int maxColumn)
        {
            _starts = new int[maxColumn + 1];
            _lengths = new int[maxColumn + 1];
        }

        /// <summary>How many of the wanted columns the line actually has.</summary>
        public int Count { get; private set; }

        public void Read(string line)
        {
            _line = line;
            Count = 0;

            ReadOnlySpan<char> span = line;
            int position = 0;
            while (Count < _starts.Length)
            {
                int caret = span[position..].IndexOf('^');
                _starts[Count] = position;
                if (caret < 0)
                {
                    _lengths[Count] = span.Length - position;
                    Count++;
                    return;
                }

                _lengths[Count] = caret;
                Count++;
                position += caret + 1;
            }
        }

        public string Get(int column) => _line.Substring(_starts[column], _lengths[column]);

        /// <summary>False when the line is too short or the column is not an integer.</summary>
        public bool TryInt(int column, out int value)
        {
            if (column >= Count)
            {
                value = 0;
                return false;
            }

            return int.TryParse(_line.AsSpan(_starts[column], _lengths[column]), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }
}
