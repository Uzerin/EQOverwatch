namespace EQOverwatch.Core.Spells;

/// <summary>
/// The resist a spell checks, as numbered in the spell file: 1 magic, 2 fire, 3 cold, 4 poison, 5 disease, 6 chromatic, 7 prismatic,
/// 8 physical, 9 corruption. 0 means unresistable (or not known).
/// </summary>
public static class SpellResist
{
    public const int Magic = 1;
    public const int Fire = 2;
    public const int Cold = 3;
    public const int Poison = 4;
    public const int Disease = 5;
    public const int Chromatic = 6;
    public const int Prismatic = 7;
    public const int Physical = 8;
    public const int Corruption = 9;

    /// <summary>Every resist type, in the order tables list them.</summary>
    public static readonly IReadOnlyList<int> All = new[] { Magic, Fire, Cold, Poison, Disease, Chromatic, Prismatic, Physical, Corruption };

    /// <summary>The five shown as columns on the Mob History tab.</summary>
    public static readonly IReadOnlyList<int> Main = new[] { Magic, Fire, Cold, Poison, Disease };

    public static bool IsResistable(int type) => type is >= Magic and <= Corruption;

    public static string Name(int type) => type switch
    {
        Magic => "Magic",
        Fire => "Fire",
        Cold => "Cold",
        Poison => "Poison",
        Disease => "Disease",
        Chromatic => "Chromatic",
        Prismatic => "Prismatic",
        Physical => "Physical",
        Corruption => "Corruption",
        _ => "Unresistable",
    };

    /// <summary>The one-letter code used in shared resist tokens.</summary>
    public static char Code(int type) => type switch
    {
        Magic => 'M',
        Fire => 'F',
        Cold => 'C',
        Poison => 'P',
        Disease => 'D',
        Chromatic => 'H',
        Prismatic => 'R',
        Physical => 'Y',
        Corruption => 'O',
        _ => '?',
    };

    /// <summary>The resist type for a token code, or 0 when the code is not one.</summary>
    public static int FromCode(char code) => char.ToUpperInvariant(code) switch
    {
        'M' => Magic,
        'F' => Fire,
        'C' => Cold,
        'P' => Poison,
        'D' => Disease,
        'H' => Chromatic,
        'R' => Prismatic,
        'Y' => Physical,
        'O' => Corruption,
        _ => 0,
    };
}
