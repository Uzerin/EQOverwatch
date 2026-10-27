namespace EQCompanion.Core.Tests;

/// <summary>Builds spells_us.txt rows in both layouts. Unused columns hold "0", like the real files' numeric fields.</summary>
internal static class SpellTestData
{
    private const int Columns = 130;

    /// <summary>"This class cannot cast it": the value real files use in the Bard class-level column.</summary>
    public const int NoBardLevel = 255;

    /// <summary>
    /// Unified layout: 6 cast-on-you, 7 cast-on-other, 8 fades, 13 cast time, 16 formula, 17 max ticks,
    /// 83 GoodEffect, 115 Bard level.
    /// </summary>
    public static string LegacyRow(
        int id, string name, string castOnYou, string castOnOther, string fades, int castMs, int formula, int ticks,
        int goodEffect = 1, int bardLevel = NoBardLevel)
    {
        string[] c = Enumerable.Repeat("0", Columns).ToArray();
        c[0] = id.ToString();
        c[1] = name;
        c[6] = castOnYou;
        c[7] = castOnOther;
        c[8] = fades;
        c[13] = castMs.ToString();
        c[16] = formula.ToString();
        c[17] = ticks.ToString();
        c[83] = goodEffect.ToString();
        c[115] = bardLevel.ToString();
        return string.Join('^', c);
    }

    /// <summary>Split layout: 8 cast time, 11 formula, 12 max ticks, 28 GoodEffect, 43 Bard level, 10 recast (ms). Column 6 stays numeric.</summary>
    public static string SplitRow(int id, string name, int castMs, int formula, int ticks, int goodEffect = 1, int bardLevel = NoBardLevel, int recastMs = 0)
    {
        string[] c = Enumerable.Repeat("0", Columns).ToArray();
        c[0] = id.ToString();
        c[1] = name;
        c[10] = recastMs.ToString();
        c[8] = castMs.ToString();
        c[11] = formula.ToString();
        c[12] = ticks.ToString();
        c[28] = goodEffect.ToString();
        c[43] = bardLevel.ToString();
        return string.Join('^', c);
    }

    /// <summary>A legacy row cut off after column 20, like an old file without the later columns.</summary>
    public static string ShortLegacyRow(int id, string name, string castOnYou, string castOnOther, string fades, int castMs, int formula, int ticks)
    {
        string[] c = LegacyRow(id, name, castOnYou, castOnOther, fades, castMs, formula, ticks).Split('^');
        return string.Join('^', c.Take(21));
    }

    public static string WriteFile(string path, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }
}
