using EQCompanion.Core.Models;

namespace EQCompanion.Core.Spells;

/// <summary>
/// One spell, song or damage-over-time that has a real duration. Instant and permanent spells are never loaded here
/// (see <see cref="SpellDefinition"/> for the list that keeps them).
/// </summary>
/// <param name="CastOnYou">Message shown when the spell lands on you (exact log text).</param>
/// <param name="CastOnOther">Message appended to a target's name when the spell lands on someone else (e.g. " looks faster.").</param>
/// <param name="SpellFades">Message shown when the spell wears off.</param>
/// <param name="CastTimeMs">Cast time in milliseconds.</param>
/// <param name="BaseDurationSeconds">Duration at the database's caster level: ticks x 6, with no focus or AA extensions.</param>
/// <param name="Category">Overlay the spell's timer belongs to: Songs for bard songs, Debuffs for detrimental spells, otherwise Buffs.</param>
public sealed record SpellInfo(
    int SpellId,
    string Name,
    string CastOnYou,
    string CastOnOther,
    string SpellFades,
    int CastTimeMs,
    int DurationFormula,
    int MaxDurationTicks,
    int BaseDurationSeconds,
    TimerOverlayCategory Category = TimerOverlayCategory.Buffs)
{
    /// <summary><see cref="CastOnOther"/> without surrounding whitespace: the text a log line ends with.</summary>
    public string CastOnOtherSuffix => CastOnOther.Trim();
}

/// <summary>
/// A spell as the raid-boss picker sees it: any spell with at least one landing message, including instant casts such as
/// boss AE nukes and knockbacks that <see cref="SpellInfo"/> leaves out.
/// </summary>
public sealed record SpellDefinition(int SpellId, string Name, string CastOnYou, string CastOnOther)
{
    /// <summary>"Dragon Roar  (#1234)".</summary>
    public string Label => $"{Name}  (#{SpellId})";
}

/// <summary>
/// A spell-file row with a real recast time (a discipline, or any spell that cannot be recast right away), as the AAs &amp; Discs picker
/// sees it. <see cref="ActiveSeconds"/> is how long it lasts (0 when it has no duration).
/// </summary>
public sealed record AbilityInfo(int SpellId, string Name, int RecastSeconds, int ActiveSeconds)
{
    /// <summary>"Defensive Discipline  (#4499)".</summary>
    public string Label => $"{Name}  (#{SpellId})";
}

/// <summary>Immutable, indexed set of duration spells. Indexes that can match several spells list the longest-lasting first.</summary>
public sealed class SpellDatabase
{
    public static readonly SpellDatabase Empty = new(Array.Empty<SpellInfo>(), SpellDatabaseService.DefaultCasterLevel);

    private static readonly SpellInfo[] NoSpells = Array.Empty<SpellInfo>();

    private readonly Dictionary<int, SpellInfo> _byId = new();
    private readonly Dictionary<int, SpellDefinition> _definitionsById = new();
    private readonly Dictionary<string, SpellInfo> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SpellInfo[]> _byCastOnYou;
    private readonly Dictionary<string, SpellInfo[]> _byCastOnOther;
    private readonly Dictionary<string, SpellInfo[]> _bySpellFades;

    /// <param name="definitions">
    /// Every spell with a landing message, duration or not. When null they are derived from <paramref name="spells"/>.
    /// </param>
    public SpellDatabase(
        IEnumerable<SpellInfo> spells,
        int casterLevel,
        IEnumerable<SpellDefinition>? definitions = null,
        IEnumerable<AbilityInfo>? abilities = null)
    {
        CasterLevel = casterLevel;

        // One entry per name (several ranks share one): the one with the longest recast, then the lowest id.
        var abilityByName = new Dictionary<string, AbilityInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (AbilityInfo ability in abilities ?? Enumerable.Empty<AbilityInfo>())
        {
            if (!abilityByName.TryGetValue(ability.Name, out AbilityInfo? existing)
                || ability.RecastSeconds > existing.RecastSeconds
                || (ability.RecastSeconds == existing.RecastSeconds && ability.SpellId < existing.SpellId))
            {
                abilityByName[ability.Name] = ability;
            }
        }

        var abilityList = new List<AbilityInfo>(abilityByName.Values);
        abilityList.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        Abilities = abilityList;

        foreach (SpellInfo spell in spells)
        {
            _byId[spell.SpellId] = spell;
        }

        var list = new List<SpellInfo>(_byId.Values);
        list.Sort(static (a, b) =>
        {
            int byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return byName != 0 ? byName : a.SpellId.CompareTo(b.SpellId);
        });
        Spells = list;

        foreach (SpellInfo spell in list)
        {
            // Several ranks can share a name; keep the longest-lasting one.
            if (!_byName.TryGetValue(spell.Name, out SpellInfo? existing) || IsLonger(spell, existing))
            {
                _byName[spell.Name] = spell;
            }
        }

        IEnumerable<SpellDefinition> source = definitions
            ?? list.Select(s => new SpellDefinition(s.SpellId, s.Name, s.CastOnYou, s.CastOnOther));
        foreach (SpellDefinition definition in source)
        {
            if (definition.CastOnYou.Length > 0 || definition.CastOnOther.Trim().Length > 0)
            {
                _definitionsById[definition.SpellId] = definition;
            }
        }

        var all = new List<SpellDefinition>(_definitionsById.Values);
        all.Sort(static (a, b) =>
        {
            int byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return byName != 0 ? byName : a.SpellId.CompareTo(b.SpellId);
        });
        AllSpellDefinitions = all;

        _byCastOnYou = Index(list, s => s.CastOnYou);
        _byCastOnOther = Index(list, s => s.CastOnOtherSuffix);
        _bySpellFades = Index(list, s => s.SpellFades);
    }

    /// <summary>The caster level the durations were calculated for.</summary>
    public int CasterLevel { get; }

    /// <summary>Every loaded duration spell, ordered by name then id.</summary>
    public IReadOnlyList<SpellInfo> Spells { get; }

    /// <summary>
    /// Every spell that has a landing message (including instant and permanent ones), ordered by name then id.
    /// Feeds the raid boss spell picker.
    /// </summary>
    public IReadOnlyList<SpellDefinition> AllSpellDefinitions { get; }

    /// <summary>
    /// Every row with a recast time of at least <see cref="SpellDatabaseService.MinAbilityRecastSeconds"/> seconds, one per name,
    /// ordered by name. Only the split spell-file layout carries the recast time; the list is empty otherwise.
    /// </summary>
    public IReadOnlyList<AbilityInfo> Abilities { get; }

    public int Count => Spells.Count;

    public SpellInfo? FindById(int spellId) => _byId.GetValueOrDefault(spellId);

    /// <summary>Case-insensitive.</summary>
    public AbilityInfo? FindAbility(string name) =>
        name is null ? null : Abilities.FirstOrDefault(a => a.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public SpellDefinition? FindDefinitionById(int spellId) => _definitionsById.GetValueOrDefault(spellId);

    /// <summary>Case-insensitive.</summary>
    public SpellInfo? FindByName(string name) => name is null ? null : _byName.GetValueOrDefault(name);

    /// <summary>Spells whose landing-on-you text is exactly <paramref name="text"/>, longest duration first.</summary>
    public IReadOnlyList<SpellInfo> FindByCastOnYou(string text) => Lookup(_byCastOnYou, text);

    /// <summary>Spells whose landing-on-other suffix equals <paramref name="suffix"/> (whitespace-trimmed), longest duration first.</summary>
    public IReadOnlyList<SpellInfo> FindByCastOnOther(string suffix) => Lookup(_byCastOnOther, suffix?.Trim());

    /// <summary>Spells whose wear-off text is exactly <paramref name="text"/>, longest duration first.</summary>
    public IReadOnlyList<SpellInfo> FindBySpellFades(string text) => Lookup(_bySpellFades, text);

    private static bool IsLonger(SpellInfo candidate, SpellInfo existing) =>
        candidate.BaseDurationSeconds > existing.BaseDurationSeconds;

    private static IReadOnlyList<SpellInfo> Lookup(Dictionary<string, SpellInfo[]> index, string? key) =>
        key is not null && index.TryGetValue(key, out SpellInfo[]? spells) ? spells : NoSpells;

    private static Dictionary<string, SpellInfo[]> Index(IEnumerable<SpellInfo> spells, Func<SpellInfo, string> key)
    {
        return spells
            .Where(s => !string.IsNullOrEmpty(key(s)))
            .GroupBy(key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.BaseDurationSeconds).ThenBy(s => s.SpellId).ToArray(),
                StringComparer.Ordinal);
    }
}
