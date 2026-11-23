using System.Globalization;

namespace EQOverwatch.Core.Tlp;

/// <summary>
/// One unlock of a TLP server: the expansions (numbers as in <see cref="Maps.ZoneDirectory.ExpansionNames"/>, 1 = Classic), the day, the
/// level cap it brings (null: unchanged or not known), the server bonuses it adds, and whether the day is only worked out from the
/// server's cadence (<see cref="Estimated"/>; shown with "?").
/// </summary>
public sealed record TlpUnlock(IReadOnlyList<int> Expansions, DateOnly? Date, int? LevelCap, string Bonus = "", bool Estimated = false, string Note = "")
{
    /// <summary>"Planes of Power + The Legacy of Ykesha", or the note alone ("Level 55") when no expansion opens.</summary>
    public string Title => Expansions.Count == 0 ? Note : string.Join(" + ", Expansions.Select(Maps.ZoneDirectory.ExpansionName));
}

/// <summary>A challenge achievement of a level-locked server: the level, the expansions, the goal and the reward.</summary>
public sealed record TlpChallenge(int Level, string Expansions, string Goal, string Reward);

/// <summary>
/// A TLP server's published ruleset: its rules as the official posts (and, where noted, Allakhazam) give them, its unlocks, and its base
/// experience rate over time. A rule that is not listed is not part of the server.
/// </summary>
public sealed record TlpServer(
    string Key,
    string Name,
    IReadOnlyList<string> Rules,
    string Source,
    IReadOnlyList<TlpUnlock> Unlocks,
    IReadOnlyList<(DateOnly From, double Rate)> Experience,
    bool Legacy = false,
    bool ResourceHunter = false,
    bool Challenges = false,
    IReadOnlyList<TlpChallenge>? ChallengeList = null)
{
    /// <summary>True when some unlock adds server bonuses (only evolving-ruleset servers do).</summary>
    public bool HasBonuses => Unlocks.Any(u => u.Bonus.Length > 0);

    /// <summary>The unlocks that have happened by <paramref name="today"/> (unknown past days count as open).</summary>
    public IEnumerable<TlpUnlock> Open(DateOnly today)
    {
        for (int i = 0; i < Unlocks.Count; i++)
        {
            TlpUnlock u = Unlocks[i];
            bool later = Unlocks.Skip(i + 1).Any(n => n.Date is { } d && !n.Estimated && d <= today);
            if ((u.Date is { } date && date <= today) || (u.Date is null && later))
            {
                yield return u;
            }
        }
    }

    /// <summary>The next unlock after <paramref name="today"/>, or null at the end of the schedule.</summary>
    public TlpUnlock? Next(DateOnly today)
    {
        var open = Open(today).ToList();
        return Unlocks.FirstOrDefault(u => !open.Contains(u));
    }

    /// <summary>Every expansion open on <paramref name="today"/>.</summary>
    public IReadOnlySet<int> OpenExpansions(DateOnly today) => Open(today).SelectMany(u => u.Expansions).ToHashSet();

    /// <summary>The level cap on <paramref name="today"/>, or null when not known.</summary>
    public int? LevelCap(DateOnly today) => Open(today).Select(u => u.LevelCap).LastOrDefault(c => c is not null);

    /// <summary>The server's experience rate (percent of normal) on a day, or null when the server publishes none.</summary>
    public double? ExperienceOn(DateOnly day) =>
        Experience.Count == 0 ? null : Experience.LastOrDefault(e => e.From <= day) is { From.Year: > 1 } step ? step.Rate : Experience[0].Rate;

    /// <summary>The bonuses in effect: each kind's latest value from the open unlocks ("EXP 115%", "Loot 125%", …), in first-seen order.</summary>
    public IReadOnlyList<string> CurrentBonuses(DateOnly today)
    {
        var byKind = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (string part in Open(today).SelectMany(u => u.Bonus.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            int space = part.LastIndexOf(' ');
            string kind = space > 0 ? part[..space] : part;
            if (!byKind.ContainsKey(kind))
            {
                order.Add(kind);
            }

            byKind[kind] = part;
        }

        return order.Select(k => byKind[k]).ToList();
    }
}

/// <summary>The rulesets of the TLP servers, from the official forum posts (Darkpaw) and, where they have no dates, Allakhazam.</summary>
public static class TlpServers
{
    /// <summary>The original level cap of each expansion (index = expansion number; 0 unused; null when not known).</summary>
    private static readonly int?[] Caps =
    {
        null, 50, 60, 60, 60, 65, 65, 65, 65, 70, 70, 70, 70, 75, 75, 80, 85, 85, 90, 95, 100, 100, 105, 105, 105, 110, 110, 115, 115, 120, 120,
        125, 125, null,
    };

    /// <summary>The level cap an expansion launched with, or null.</summary>
    public static int? OriginalCap(int expansion) => expansion >= 1 && expansion < Caps.Length ? Caps[expansion] : null;

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static TlpUnlock U(int[] expansions, DateOnly? date, string bonus = "", int? cap = null, bool estimated = false, string note = "") =>
        new(expansions, date, cap ?? expansions.Select(OriginalCap).Where(c => c is not null).DefaultIfEmpty(null).Max(), bonus, estimated, note);

    /// <summary>Standard progression experience: 25% at launch, 50% with Gates of Discord, 75% with Depths of Darkhollow, 100% with The Serpent's Spine.</summary>
    private static IReadOnlyList<(DateOnly, double)> Standard(DateOnly launch, IReadOnlyList<TlpUnlock> unlocks)
    {
        DateOnly When(int expansion, DateOnly fallback) => DayOf(unlocks, expansion) ?? fallback;
        return new (DateOnly, double)[]
        {
            (launch, 25), (When(8, DateOnly.MaxValue), 50), (When(11, DateOnly.MaxValue), 75), (When(13, DateOnly.MaxValue), 100),
        };
    }

    /// <summary>The day an expansion opened; one no post gives counts from the next known day after it (it had happened by then).</summary>
    private static DateOnly? DayOf(IReadOnlyList<TlpUnlock> unlocks, int expansion)
    {
        int i = unlocks.ToList().FindIndex(u => u.Expansions.Contains(expansion));
        return i < 0 ? null : unlocks.Skip(i).Select(u => u.Date).FirstOrDefault(d => d is not null);
    }

    /// <summary>
    /// Fills the days of a standard-cadence schedule: <paramref name="anchors"/> are the days the official posts give; later unlocks
    /// follow 12 weeks after one that raised the level cap and 8 weeks otherwise (shown with "?"); earlier ones without a day stay open.
    /// </summary>
    private static List<TlpUnlock> Cadence(IEnumerable<int[]> steps, IReadOnlyDictionary<int, DateOnly> anchors)
    {
        var list = new List<TlpUnlock>();
        foreach (int[] step in steps)
        {
            int? cap = step.Select(OriginalCap).Where(c => c is not null).DefaultIfEmpty(null).Max();
            DateOnly? date = null;
            bool estimated = false;
            if (anchors.TryGetValue(step[0], out DateOnly anchor))
            {
                date = anchor;
            }
            else if (list.Count > 0 && list[^1].Date is { } previous)
            {
                // The previous unlock lasted 12 weeks when it raised the level cap, 8 otherwise.
                bool raised = list.Count == 1 || list[^1].LevelCap > list[^2].LevelCap;
                date = previous.AddDays(raised ? 84 : 56);
                estimated = true;
            }

            list.Add(new TlpUnlock(step, date, cap, Estimated: estimated));
        }

        // Worked-out days before the last day the posts give are not shown: those unlocks are simply open.
        DateOnly lastAnchor = anchors.Values.Max();
        return list.Select(u => u.Estimated && u.Date < lastAnchor ? u with { Date = null, Estimated = false } : u).ToList();
    }

    private static readonly int[][] FromOmens =
    {
        new[] { 9, 10 }, new[] { 11 }, new[] { 12 }, new[] { 13 }, new[] { 14 }, new[] { 15 }, new[] { 16 }, new[] { 17 }, new[] { 18 }, new[] { 19 },
        new[] { 20 }, new[] { 21 }, new[] { 22 }, new[] { 23 }, new[] { 24 }, new[] { 25 }, new[] { 26 }, new[] { 27 }, new[] { 28 }, new[] { 29 },
        new[] { 30 }, new[] { 31 }, new[] { 32 }, new[] { 33 },
    };

    private static IEnumerable<int[]> Classic() =>
        new[] { new[] { 1 }, new[] { 2 }, new[] { 3 }, new[] { 4 }, new[] { 5, 6 }, new[] { 7 }, new[] { 8 } }.Concat(FromOmens);

    public static IReadOnlyList<TlpServer> All { get; } = Build();

    /// <summary>The ruleset of a server ("frostreaver", any case), or null for servers without one.</summary>
    public static TlpServer? Find(string? server) =>
        server is null ? null : All.FirstOrDefault(s => s.Key.Equals(server.Trim(), StringComparison.OrdinalIgnoreCase)
                                                        || (s.Key == "teek" && server.Trim().Equals("tormax", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The Pacific day of a moment (unlocks and the Resource Hunter day both follow Pacific time).</summary>
    public static DateOnly Today(DateTime utc) => DateOnly.ParseExact(ResourceHunter.ResourceHunterText.Day(utc), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static List<TlpServer> Build()
    {
        var servers = new List<TlpServer>();

        // ── Frostreaver (Frostreaver Rulesets FAQ) ──
        var frost = new List<TlpUnlock>
        {
            U(new[] { 1, 2, 3 }, D(2026, 5, 27), "Loot 125%, Faction 200%"),
            U(new[] { 4 }, D(2026, 10, 1), "AA EXP 175%"),
            U(new[] { 5, 6 }, D(2027, 2, 4), "Coin 150%, NPC spawn time 85%"),
            U(new[] { 7 }, D(2027, 4, 1), "Alternate currency 150%"),
            U(new[] { 8 }, D(2027, 4, 29), "EXP 115%, Tribute donation 200%"),
            U(new[] { 9, 10 }, D(2027, 6, 24), "Rare spawn 200%, Faction 300%"),
            U(new[] { 11, 12 }, D(2027, 8, 19), "EXP 130%, Evolving item EXP 150%, Skill 200%"),
            U(new[] { 13 }, D(2027, 10, 14), "EXP 145%, Loot 200%"),
            U(new[] { 14 }, D(2027, 12, 9), "Alternate currency 200%"),
            U(new[] { 15 }, D(2028, 2, 3), "Faction 400%"),
            U(new[] { 16 }, D(2028, 3, 30), "EXP 160%"),
            U(new[] { 17 }, D(2028, 5, 25), "Tribute use 75%"),
            U(new[] { 18 }, D(2028, 7, 20), "AA EXP 200%"),
            U(new[] { 19 }, D(2028, 9, 14), "Alternate currency 250%"),
            U(new[] { 20 }, D(2028, 11, 9), "Collection spawn time 75%"),
            U(new[] { 21 }, D(2029, 1, 4), "Mercenary AA EXP 200%"),
            U(new[] { 22 }, D(2029, 3, 1), "NPC spawn time 70%"),
            U(new[] { 23 }, D(2029, 4, 26), "EXP 180%"),
            U(new[] { 24 }, D(2029, 6, 21), "Rare spawn 300%"),
            U(new[] { 25 }, D(2029, 8, 16), "AA EXP 225%"),
            U(new[] { 26 }, D(2029, 10, 11), "Luck 125%"),
            U(new[] { 27 }, D(2029, 12, 6), "Evolving item EXP 200%"),
            U(new[] { 28 }, D(2030, 1, 31), "AA EXP 250%"),
            U(new[] { 29 }, D(2030, 3, 28), "EXP 200%"),
            U(new[] { 30 }, D(2030, 5, 23), "Rare spawn 400%"),
            U(new[] { 31 }, D(2030, 7, 18)),
            U(new[] { 32 }, D(2030, 9, 12)),
            U(new[] { 33 }, D(2030, 11, 7)),
        };
        servers.Add(new TlpServer(
            "frostreaver", "Frostreaver",
            new[]
            {
                "Launched May 27, 2026 with Classic, Kunark and Velious; expansions every 8 weeks after Planes of Power, until Live.",
                "Experience: Live rate, raised by the evolving ruleset (EXP bonuses below).",
                "Legacy Characters: each max-level character gives the account 10% more experience, up to 100%; it starts over when the level cap rises.",
                "Resource Hunter: up to 3 zones of each expansion get a random bonus every day at midnight Pacific.",
                "Random loot: rare NPCs and raids drop loot from other NPCs of similar level in the same expansion; rare NPCs spawn more often.",
                "Encounter locking: an NPC belongs to whoever engaged it; /yell semi-unlocks it.",
                "Free trade. No truebox. Item level requirements off. Early item focus effects.",
                "Challenge achievements for beating content 10 levels below the expansion's original cap.",
                "Agents of Change and pick zones.",
            },
            "Frostreaver Rulesets FAQ (forums.everquest.com)",
            frost,
            new (DateOnly, double)[] { (D(2026, 5, 27), 100), (D(2027, 4, 29), 115), (D(2027, 8, 19), 130), (D(2027, 10, 14), 145), (D(2028, 3, 30), 160), (D(2029, 4, 26), 180), (D(2030, 3, 28), 200) },
            Legacy: true, ResourceHunter: true, Challenges: true));

        // ── Fangbreaker (Fangbreaker Rulesets FAQ) ──
        var fang = new List<TlpUnlock>
        {
            U(new[] { 1, 2, 3, 4 }, D(2025, 5, 28), cap: 50, note: "Expanded start"),
            U(Array.Empty<int>(), D(2025, 7, 24), cap: 51, note: "Level 51"),
            U(Array.Empty<int>(), D(2025, 9, 18), cap: 55, note: "Level 55"),
            U(Array.Empty<int>(), D(2025, 11, 13), cap: 60, note: "Level 60"),
            U(new[] { 5, 6 }, D(2026, 2, 5)), U(new[] { 7 }, D(2026, 4, 30)), U(new[] { 8 }, D(2026, 5, 28)), U(new[] { 9, 10 }, D(2026, 7, 23)),
            U(new[] { 11 }, D(2026, 10, 15)), U(new[] { 12 }, D(2026, 12, 10)), U(new[] { 13 }, D(2027, 2, 4)), U(new[] { 14 }, D(2027, 4, 29)),
            U(new[] { 15 }, D(2027, 6, 24)), U(new[] { 16 }, D(2027, 9, 16)), U(new[] { 17 }, D(2027, 12, 9)), U(new[] { 18 }, D(2028, 2, 3)),
            U(new[] { 19 }, D(2028, 4, 27)), U(new[] { 20 }, D(2028, 7, 20)), U(new[] { 21 }, D(2028, 10, 12)), U(new[] { 22 }, D(2028, 12, 7)),
            U(new[] { 23 }, D(2029, 3, 1)), U(new[] { 24 }, D(2029, 4, 26)), U(new[] { 25 }, D(2029, 6, 21)), U(new[] { 26 }, D(2029, 9, 13)),
            U(new[] { 27 }, D(2029, 11, 8)), U(new[] { 28 }, D(2030, 1, 31)), U(new[] { 29 }, D(2030, 3, 28)), U(new[] { 30 }, D(2030, 6, 20)),
            U(new[] { 31 }, D(2030, 8, 15)), U(new[] { 32 }, D(2030, 11, 7)),
        };
        servers.Add(new TlpServer(
            "fangbreaker", "Fangbreaker",
            new[]
            {
                "Launched May 28, 2025 with Classic through Luclin, level-locked to 50, then 51, 55 and 60 before Planes of Power.",
                "Experience: standard progression rate (25%, 50% with Gates of Discord, 75% with Depths of Darkhollow, 100% with The Serpent's Spine).",
                "Legacy Characters: 10% per max-level character, up to 100% (AA, quest, kill and mercenary AA experience).",
                "Resource Hunter: random daily zone bonuses at midnight Pacific.",
                "Encounter locking; rare and hard NPCs can only be semi-unlocked by the group or raid leader.",
                "Restricted truebox (two computers per person) until Planes of Power.",
                "Until Planes of Power: item level requirements are recommendations, and NPCs get no level-based resist bonus against player spells.",
                "Out-of-combat regen, Mitigation of the Mighty, Agents of Change and pick zones; Gloomingdeep opens with Gates of Discord.",
            },
            "Fangbreaker Rulesets FAQ (forums.everquest.com)",
            fang, Standard(D(2025, 5, 28), fang), Legacy: true, ResourceHunter: true));

        // ── Lethar (Lethar Rulesets FAQ) ──
        var lethar = new List<TlpUnlock>
        {
            U(Enumerable.Range(1, 13).ToArray(), D(2026, 8, 27)),
            U(new[] { 14 }, D(2026, 11, 19)), U(new[] { 15 }, D(2027, 1, 14)), U(new[] { 16 }, D(2027, 3, 11)), U(new[] { 17 }, D(2027, 5, 6)),
            U(new[] { 18 }, D(2027, 7, 1)), U(new[] { 19 }, D(2027, 8, 26)), U(new[] { 20 }, D(2027, 10, 21)), U(new[] { 21 }, D(2027, 12, 16)),
            U(new[] { 22 }, D(2028, 2, 10)), U(new[] { 23 }, D(2028, 4, 6)), U(new[] { 24 }, D(2028, 6, 1)), U(new[] { 25 }, D(2028, 7, 27)),
            U(new[] { 26 }, D(2028, 9, 21)), U(new[] { 27 }, D(2028, 11, 16)), U(new[] { 28 }, D(2029, 1, 11)), U(new[] { 29 }, D(2029, 3, 8)),
            U(new[] { 30 }, D(2029, 5, 3)), U(new[] { 31 }, D(2029, 6, 28)), U(new[] { 32 }, D(2029, 8, 23)), U(new[] { 33 }, D(2029, 10, 18)),
        };
        servers.Add(new TlpServer(
            "lethar", "Lethar",
            new[]
            {
                "Launched with everything through The Serpent's Spine; The Buried Sea 12 weeks later, then an expansion every 8 weeks.",
                "Experience: normal Live rate.",
                "Personal loot: groups and raids get extra items (2–3 people: 1 each, 4–5: 2, 6: 3; raids: 1 each in instanced raid zones); solo players get none.",
                "Items are Attunable and have no class, race or deity requirements; item level requirements are recommendations.",
                "Legacy Characters: 10% per max-level character, up to 100%; starts over when the level cap rises.",
                "Not free trade. No truebox. Mitigation of the Mighty.",
                "Heroic characters, Defiant and Consigned gear: about three months after launch.",
            },
            "Lethar Rulesets FAQ (forums.everquest.com)",
            lethar, new (DateOnly, double)[] { (D(2026, 8, 27), 100) }, Legacy: true));

        // ── Vaniki (official Vaniki FAQ; dates from Allakhazam) ──
        var vaniki = new List<TlpUnlock>
        {
            U(Enumerable.Range(1, 8).ToArray(), D(2022, 5, 25), cap: 40),
            U(Array.Empty<int>(), D(2022, 6, 22), cap: 50, note: "Level 50"),
            U(Array.Empty<int>(), D(2022, 7, 6), cap: 55, note: "Level 55"),
            U(new[] { 9, 10, 11, 12 }, D(2022, 8, 3), cap: 60), U(new[] { 13, 14 }, D(2022, 10, 26), cap: 65), U(new[] { 15 }, D(2023, 1, 19), cap: 70),
            U(new[] { 16, 17 }, D(2023, 3, 16), cap: 75), U(new[] { 18 }, D(2023, 6, 8), cap: 80), U(new[] { 19 }, D(2023, 8, 3), cap: 85),
            U(new[] { 20, 21 }, D(2023, 9, 28), cap: 90), U(new[] { 22, 23, 24 }, D(2023, 12, 21), cap: 95), U(new[] { 25, 26 }, D(2024, 3, 14), cap: 100),
            U(new[] { 27, 28 }, D(2024, 6, 6), cap: 105), U(new[] { 29 }, D(2024, 8, 29), cap: 110),
            U(Array.Empty<int>(), D(2024, 11, 21), cap: 115, note: "Level 115"), U(Array.Empty<int>(), D(2025, 2, 13), cap: 120, note: "Level 120"),
        };
        servers.Add(new TlpServer(
            "vaniki", "Vaniki",
            new[]
            {
                "Level-locked: launched May 25, 2022 at level 40 with everything through Gates of Discord; the cap stays 10 levels below the newest expansion's original cap.",
                "Experience: standard progression rate, starting at the Gates of Discord rate.",
                "Non-scripted, non-global loot drops are doubled.",
                "Item level requirements are recommendations; item spells need 10 levels less; zone level requirements are 10 levels lower; tasks and missions allow 10 levels more.",
                "NPCs get no level-based resist bonus against player spells; crowd control (charm, mez, stun, fear and more) works on NPCs 10 levels higher.",
                "Truebox: 1 client per computer, 3 from Prophecy of Ro, removed with The Buried Sea.",
                "Challenge achievements with account rewards (below). Illusion: Vaniki at level 60 and a weapon ornament at each later cap.",
                "Raid lockouts: 2.5 days after requesting an instance, 6.5 days per boss (Allakhazam).",
                "Mitigation of the Mighty, Challenger's Perk (no level loss), out-of-combat regen, Gloomingdeep at launch; some zones stay in their launch state until Omens of War.",
            },
            "Vaniki Server FAQ (everquest.com) and Allakhazam (unlock dates)",
            vaniki,
            new (DateOnly, double)[] { (D(2022, 5, 25), 50), (D(2022, 8, 3), 75), (D(2022, 10, 26), 100) },
            Challenges: true,
            ChallengeList: new[]
            {
                new TlpChallenge(40, "Gates of Discord", "Cazic-Thule", "Food clicky, unlimited charges"),
                new TlpChallenge(50, "Gates of Discord", "Yelinak", "Teleport - Western Wastes"),
                new TlpChallenge(55, "Gates of Discord", "Cragbeast Queen", "Raid currency"),
                new TlpChallenge(60, "OoW, DoN, DoDH, PoR", "Riftseekers", "Illusion: Vaniki"),
                new TlpChallenge(65, "TSS, TBS", "Ashengate - Lethar", "Drink clicky"),
                new TlpChallenge(70, "Secrets of Faydwer", "Meldrath's Mansion - Meldrath", "Teleport - Fortress Mechanotus"),
                new TlpChallenge(75, "SoD, Underfoot", "Korafax - Pallorax the Soul Slayer", "Raid currency"),
                new TlpChallenge(80, "House of Thule", "Fear Itself - Cazic's Final Stand", "Illusion: Gigyn"),
                new TlpChallenge(85, "Veil of Alaris", "Resplendent Temple", "Removable Purity Augment"),
                new TlpChallenge(90, "RoF, CotF", "Shard's Landing", "Teleport - Epicenter"),
                new TlpChallenge(95, "TDS, TBM, EoK", "Plane of Hate - Innoruuk", "Raid currency"),
                new TlpChallenge(100, "RoS, TBL", "Balance of Power (raid)", "Illusion: Gnomework"),
                new TlpChallenge(105, "ToV, CoV", "Griklor the Restless", "Shadowstep clicky"),
                new TlpChallenge(110, "Terror of Luclin", "Doomshade", "Teleport - Maiden's Eye"),
            }));

        // ── Teek & Tormax ──
        var teek = Cadence(
            new[] { new[] { 1, 2 }, new[] { 3 }, new[] { 4 }, new[] { 5, 6 }, new[] { 7 }, new[] { 8 } }.Concat(FromOmens),
            new Dictionary<int, DateOnly>
            {
                [1] = D(2024, 5, 22), [3] = D(2024, 8, 14), [4] = D(2024, 10, 9), [5] = D(2024, 12, 4), [7] = D(2025, 2, 26), [8] = D(2025, 3, 26),
                [9] = D(2025, 5, 21), [13] = D(2025, 12, 4), [14] = D(2026, 2, 26), [15] = D(2026, 4, 23), [16] = D(2026, 7, 16), [17] = D(2026, 10, 8),
            });
        servers.Add(new TlpServer(
            "teek", "Teek & Tormax",
            new[]
            {
                "Launched May 22, 2024 with Kunark; Velious 12 weeks later, then 8 weeks per expansion (12 when the level cap rises).",
                "Experience: standard progression rate (25%, 50% with Gates of Discord, 75% with Depths of Darkhollow, 100% with The Serpent's Spine). No Legacy experience.",
                "Teek only: random loot and free trade. Tormax has no special rules.",
                "Truebox: 1 client per computer, 3 from Omens of War, removed with The Buried Sea.",
                "Item focus effects from Shadows of Luclin on.",
            },
            "Teek and Tormax are on the Horizon (forums.everquest.com)",
            teek, Standard(D(2024, 5, 22), teek)));

        // ── Yelinak ──
        var yelinak = Cadence(Classic(), new Dictionary<int, DateOnly>
        {
            [1] = D(2022, 5, 25), [21] = D(2025, 11, 6), [23] = D(2026, 3, 26), [24] = D(2026, 5, 21), [25] = D(2026, 7, 16), [26] = D(2026, 10, 8),
        });
        servers.Add(new TlpServer(
            "yelinak", "Yelinak",
            new[]
            {
                "Standard truebox time-locked server with no special rules: launched May 25, 2022 with Classic.",
                "Kunark 8 weeks after launch, then 8 weeks per expansion (12 for Lost Dungeons of Norrath, 4 for Gates of Discord; 12 when the level cap rises).",
                "Experience: standard progression rate (25%, 50% with Gates of Discord, 75% with Depths of Darkhollow, 100% with The Serpent's Spine).",
                "Truebox: 1 client per computer, 3 from Omens of War, removed with The Buried Sea.",
            },
            "Yelinak Progression Server FAQ (forums.everquest.com)",
            yelinak, Standard(D(2022, 5, 25), yelinak)));

        // ── Oakwynd ──
        var oak = Cadence(Classic(), new Dictionary<int, DateOnly>
        {
            [1] = D(2023, 5, 24), [2] = D(2023, 7, 20), [17] = D(2025, 11, 6), [19] = D(2026, 3, 26), [20] = D(2026, 6, 18), [22] = D(2026, 11, 5),
        });
        string[] oakBonus =
        {
            "", "Loot 125%", "Faction 125%", "AA EXP 125%", "Coin 150%, NPC spawn time 85%", "Alternate currency 150%", "Tribute donation 200%",
            "Rare spawn 200%, Faction 200%", "Evolving item EXP 150%", "Skill 200%", "Loot 200%", "Alternate currency 200%", "Faction 300%", "EXP 125%",
            "Tribute use 75%", "AA EXP 150%", "Alternate currency 250%", "Collection spawn time 75%", "Mercenary AA EXP 200%", "NPC spawn time 70%",
            "EXP 150%", "Rare spawn 300%", "AA EXP 175%", "Luck 125%", "Evolving item EXP 200%", "AA EXP 200%", "EXP 200%", "Rare spawn 400%", "", "", "",
        };
        oak = oak.Select((u, i) => u with { Bonus = i < oakBonus.Length ? oakBonus[i] : string.Empty }).ToList();
        DateOnly OakDay(int expansion) => DayOf(oak, expansion) ?? DateOnly.MaxValue;
        servers.Add(new TlpServer(
            "oakwynd", "Oakwynd",
            new[]
            {
                "Evolving ruleset: launched May 24, 2023 with Classic; Kunark 8 weeks and a day later, then the standard cadence; Omens of War and Dragons of Norrath open the same day.",
                "Experience: standard progression rate, raised by the evolving ruleset's EXP bonuses below.",
                "Legacy Characters: 10% per max-level character, up to 100%; starts over at 10% when the level cap rises. Account lockouts count per character.",
                "No Drop items that are not Free-Trade No Drop are Heirloom.",
                "Encounter locking from Classic: loot and experience go to whoever does the most damage; /yell semi-unlocks.",
                "Truebox: 1 client per computer, 3 from Omens of War, removed with The Buried Sea.",
            },
            "Oakwynd Now Live! and Oakwynd Information and FAQ (forums.everquest.com)",
            oak,
            new (DateOnly, double)[] { (D(2023, 5, 24), 25), (OakDay(8), 50), (OakDay(11), 75), (OakDay(13), 100), (OakDay(16), 125), (OakDay(23), 150), (OakDay(29), 200) },
            Legacy: true));

        // ── Mischief ──
        var mischief = Cadence(Classic(), new Dictionary<int, DateOnly>
        {
            [1] = D(2021, 5, 26), [27] = D(2025, 11, 6), [28] = D(2026, 1, 29), [29] = D(2026, 3, 26), [30] = D(2026, 6, 18), [31] = D(2026, 8, 13), [32] = D(2026, 11, 5),
        });
        servers.Add(new TlpServer(
            "mischief", "Mischief",
            new[]
            {
                "Launched May 26, 2021 with Classic: 1 month of Classic, 2 of Kunark, then 2 months per expansion (3 when the level cap rises; 1 for Lost Dungeons of Norrath).",
                "Experience: Mangler progression rate.",
                "Random loot: rare NPCs and raids drop loot from other NPCs of similar level in the same expansion.",
                "Free trade. Truebox. Agents of Change and pick zones.",
            },
            "Bristlebane Be Praised: Mischief is Coming (forums.everquest.com)",
            mischief, Standard(D(2021, 5, 26), mischief)));

        // ── Aradune (Mangler and Rizlona merged into it) ──
        var aradune = Cadence(Classic(), new Dictionary<int, DateOnly>
        {
            [1] = D(2020, 5, 27), [30] = D(2025, 12, 4), [31] = D(2026, 1, 29), [32] = D(2026, 4, 23), [33] = D(2026, 6, 18),
        });
        servers.Add(new TlpServer(
            "aradune", "Aradune",
            new[]
            {
                "Launched May 27, 2020 with Classic, unlocking at Mangler's cadence; Mangler (Jul 23, 2026) and Rizlona merged into it.",
                "Experience: Mangler progression rate.",
                "Truebox: boxing more than 2 characters is penalized. Agents of Change and pick zones. Dedicated GMs.",
            },
            "Aradune & Rizlona Launch Date and Goodbye Mangler (forums.everquest.com)",
            aradune, Standard(D(2020, 5, 27), aradune)));

        // ── Agnarr (Allakhazam) ──
        var agnarr = new List<TlpUnlock>
        {
            U(new[] { 1 }, D(2017, 5, 24)), U(new[] { 2 }, D(2017, 8, 16)), U(new[] { 3 }, D(2017, 11, 8)), U(new[] { 4 }, D(2018, 1, 31)),
            U(new[] { 5 }, D(2018, 4, 25)), U(new[] { 6 }, D(2018, 7, 18)), U(new[] { 7 }, D(2018, 10, 10)),
        };
        servers.Add(new TlpServer(
            "agnarr", "Agnarr",
            new[]
            {
                "Planes of Power-locked: launched May 24, 2017 with Classic, one expansion every 12 weeks, ending with Lost Dungeons of Norrath.",
                "One account per computer.",
                "8 spell gems and 15 buff slots; leadership AAs built in (no stat leadership AAs).",
                "Raid lockouts: 2.5 days after requesting an instance, 6.5 days per boss.",
                "Many spell research recipes open three expansions after the spell.",
            },
            "Allakhazam: Progression Servers Agnarr",
            agnarr, Array.Empty<(DateOnly, double)>()));

        return servers;
    }
}
