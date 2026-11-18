using System.Globalization;
using EQOverwatch.Core.Attendance;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Progress;
using EQOverwatch.Core.Roster;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Session;
using EQOverwatch.Core.Spells;
using Xunit;
using static EQOverwatch.Core.Tests.SpellTestData;

namespace EQOverwatch.Core.Tests;

/// <summary>Spell alerts, today's Session and Spell Stats numbers, the quiet-log watch, idle raiders, pet sub-lines and rampage counts.</summary>
public sealed class DayStatsAndSpellAlertTests : IDisposable
{
    private const int Lure = 1769;
    private const int Root = 230;
    private const int Mez = 292;
    private const int Elixir = 1288;

    private static readonly DateTime T0 = new(2026, 11, 18, 20, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));
    private readonly SpellDatabaseService _spells = new();
    private readonly TriggerEngineService _engine = new();
    private readonly List<string> _spoken = new();

    public DayStatsAndSpellAlertTests()
    {
        string spells = WriteFile(Path.Combine(_dir, "spells_us.txt"),
            SplitRow(Lure, "Lure of Ice", 4500, 11, 2, goodEffect: 0, resistType: 3),
            SplitRow(Root, "Root", 2000, 11, 10, goodEffect: 0, resistType: 1, effects: "1|99|0|0|0|100"),
            SplitRow(Mez, "Mesmerize", 2000, 11, 4, goodEffect: 0, resistType: 1, effects: "1|31|0|0|0|100"),
            SplitRow(Elixir, "Celestial Elixir", 3000, 11, 10));
        WriteFile(Path.Combine(_dir, "spells_us_str.txt"),
            "#SPELLINDEX^CASTERMETXT^CASTEROTHERTXT^CASTEDMETXT^CASTEDOTHERTXT^SPELLGONE^",
            "1769^^^You succumb to the lure of ice.^ succumbs to the lure of ice.^^",
            "230^^^Your feet adhere to the ground.^ adheres to the ground.^Your feet come free.^",
            "292^^^You have been mesmerized.^ has been mesmerized.^You are no longer mesmerized.^",
            "1288^^^You feel the elixir.^ feels the elixir.^The elixir fades.^");
        _spells.Load(spells);
        _engine.TriggerFired += e => _spoken.Add(e.AudioText ?? string.Empty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private SpellAlertService Alerts() => new(_spells, _engine);

    // ─────────────────────────── spell alerts ───────────────────────────

    [Fact]
    public void A_buff_that_did_not_take_hold_is_spoken_once_a_minute_per_spell()
    {
        SpellAlertService alerts = Alerts();

        alerts.OnLine("Ream", "Your Force of Nature spell did not take hold on Stackprime. (Blocked by Avatar.)", T0);
        alerts.OnLine("Ream", "Your Force of Nature spell did not take hold on Oodles. (Blocked by Avatar.)", T0.AddSeconds(20));
        alerts.OnLine("Ream", "Your Skin like Wood spell did not take hold. (Blocked by Ancient: Gift of Aegolism.)", T0.AddSeconds(25));
        alerts.OnLine("Ream", "Your Force of Nature spell did not take hold on Stackprime. (Blocked by Avatar.)", T0.AddSeconds(61));

        Assert.Equal(
            new[] { "Force of Nature blocked by Avatar", "Skin like Wood blocked by Ancient: Gift of Aegolism", "Force of Nature blocked by Avatar" },
            _spoken);
    }

    [Fact]
    public void Three_resists_in_a_row_on_one_mob_are_spoken_and_a_landing_starts_the_count_again()
    {
        SpellAlertService alerts = Alerts();

        alerts.OnLine("Myriad", "A deepspore mushroom resisted your Lure of Ice!", T0);
        alerts.OnLine("Myriad", "A deepspore mushroom resisted your Lure of Ice!", T0.AddSeconds(6));
        alerts.OnLine("Myriad", "You begin casting Lure of Ice.", T0.AddSeconds(10));
        alerts.OnLine("Myriad", "A deepspore mushroom succumbs to the lure of ice.", T0.AddSeconds(14));
        alerts.OnLine("Myriad", "A deepspore mushroom resisted your Lure of Ice!", T0.AddSeconds(20));
        alerts.OnLine("Myriad", "A thought horror resisted your Lure of Ice!", T0.AddSeconds(21));
        Assert.Empty(_spoken);

        alerts.OnLine("Myriad", "A deepspore mushroom resisted your Lure of Ice!", T0.AddSeconds(26));
        alerts.OnLine("Myriad", "A deepspore mushroom resisted your Lure of Ice!", T0.AddSeconds(32));

        Assert.Equal(new[] { "Lure of Ice resisted 3 times" }, _spoken);
    }

    [Fact]
    public void Only_your_own_character_falling_to_the_ground_is_a_failed_feign_death()
    {
        SpellAlertService alerts = Alerts();

        alerts.OnLine("Oodles", "Wait has fallen to the ground.", T0);
        alerts.OnLine("Oodles", "Oodles has fallen to the ground.", T0.AddSeconds(1));

        Assert.Equal(new[] { "Oodles feign death failed" }, _spoken);
    }

    [Fact]
    public void Your_root_and_mez_wearing_off_are_spoken_but_other_spells_are_not()
    {
        SpellAlertService alerts = Alerts();

        alerts.OnLine("Ream", "Your Celestial Elixir spell has worn off of Stackprime.", T0);
        alerts.OnLine("Ream", "Your Root spell has worn off of a timeless golem.", T0.AddSeconds(1));
        alerts.OnLine("Ream", "Your Mesmerize spell has worn off of a temple guard.", T0.AddSeconds(2));

        Assert.Equal(new[] { "Root broke on a timeless golem", "Mez broke on a temple guard" }, _spoken);
        Assert.Contains(_engine.Snapshot.Timers, t => t.Kind == TimerKind.TextAlert && t.Text == "Root broke on a timeless golem");
    }

    [Fact]
    public void Each_spell_alert_has_its_own_switch()
    {
        SpellAlertService alerts = Alerts();
        alerts.TakeHoldAlert = false;
        alerts.ResistAlert = false;
        alerts.FeignDeathAlert = false;
        alerts.RootMezAlert = false;

        alerts.OnLine("Ream", "Your Skin like Wood spell did not take hold. (Blocked by Ancient: Gift of Aegolism.)", T0);
        for (int i = 0; i < 3; i++)
        {
            alerts.OnLine("Ream", "A deepspore mushroom resisted your Lure of Ice!", T0.AddSeconds(i));
        }

        alerts.OnLine("Ream", "Ream has fallen to the ground.", T0.AddSeconds(5));
        alerts.OnLine("Ream", "Your Root spell has worn off of a timeless golem.", T0.AddSeconds(6));

        Assert.Empty(_spoken);
    }

    // ─────────────────────────── today's numbers ───────────────────────────

    private static readonly DateTime Day = new(2026, 11, 18);

    [Fact]
    public void The_day_adds_up_play_time_experience_money_kills_skill_ups_and_zones()
    {
        var day = new DayAccumulator("Stackprime");
        day.Line("Someone says, 'hello'", Day.AddHours(18).AddMinutes(58));
        day.Line("You have entered Plane of Knowledge.", Day.AddHours(19));
        day.Line("Someone says, 'hello'", Day.AddHours(19).AddMinutes(4));
        day.Line("You have entered Sleeper's Tomb.", Day.AddHours(19).AddMinutes(8));
        day.Line("You gain party experience! (7.800%)", Day.AddHours(19).AddMinutes(10));
        day.Line("You gain raid experience! (0.250%)", Day.AddHours(19).AddMinutes(12));
        day.Line("You receive  104 platinum 4 gold 4 copper from Tobart Dirkins for the contents of your bag.", Day.AddHours(19).AddMinutes(13));
        day.Line("Alive group members received 1 platinum, 1 gold and 8 copper as their share of the split from the corpse.", Day.AddHours(19).AddMinutes(14));
        day.Line("You have become better at 1H Blunt! (102)", Day.AddHours(19).AddMinutes(15));
        day.Line("a ghoul was slain by Stackprime!", Day.AddHours(19).AddMinutes(15));
        day.Line("You have slain a barbed bone skeleton!", Day.AddHours(19).AddMinutes(15));
        day.Line("Ream has been slain by a ghoul!", Day.AddHours(19).AddMinutes(15)); // a player dying is not a kill
        day.Line("Someone says, 'hello'", Day.AddHours(21)); // after 1:45 away: not play time
        day.Line("Someone says, 'hello'", Day.AddHours(21).AddMinutes(3));

        SessionSummary s = day.Summary(Day.AddHours(23));

        Assert.Equal((17 + 3) * 60, s.PlayedSeconds);
        Assert.Equal(8.05, s.XpPercent, 3);
        Assert.Equal(2, s.Kills);
        Assert.Equal(1, s.SkillUps);
        Assert.Equal(104_404 + 1_108, s.Copper);
        Assert.Equal(new[] { "", "Plane of Knowledge", "Sleeper's Tomb", "Sleeper's Tomb" }, s.Zones.Select(z => z.Zone));
        Assert.Equal(Day.AddHours(19).AddMinutes(15), s.Zones[2].End); // the stretch ends when the log went quiet
        Assert.Equal((Day.AddHours(21), Day.AddHours(21).AddMinutes(3)), (s.Zones[3].Start, s.Zones[3].End));
    }

    [Fact]
    public void The_day_counts_casts_fizzles_interrupts_blocks_and_resists_per_mob()
    {
        var day = new DayAccumulator("Myriad", _spells.Database.FindByName);
        DateTime t = Day.AddHours(20);
        day.Line("You begin casting Lure of Ice.", t);
        day.Line("A deepspore mushroom succumbs to the lure of ice.", t.AddSeconds(5));
        day.Line("You begin casting Lure of Ice.", t.AddSeconds(10));
        day.Line("A deepspore mushroom resisted your Lure of Ice!", t.AddSeconds(15));
        day.Line("You begin casting Lure of Ice.", t.AddSeconds(20));
        day.Line("Your Lure of Ice spell fizzles!", t.AddSeconds(21));
        day.Line("You begin casting Lure of Ice.", t.AddSeconds(30));
        day.Line("Your Lure of Ice spell is interrupted.", t.AddSeconds(31));
        day.Line("You begin casting Skin like Wood.", t.AddSeconds(40));
        day.Line("Your Skin like Wood spell did not take hold. (Blocked by Ancient: Gift of Aegolism.)", t.AddSeconds(43));

        SpellDayRow lure = Assert.Single(day.Spells(), s => s.Spell == "Lure of Ice");
        Assert.Equal((4, 1, 1), (lure.Casts, lure.Fizzles, lure.Interrupts));
        Assert.Equal(25, lure.FizzlePercent, 3);
        SpellDayRow skin = Assert.Single(day.Spells(), s => s.Spell == "Skin like Wood");
        Assert.Equal((1, "Ancient: Gift of Aegolism"), (skin.Blocked, skin.BlockedBy));

        ResistDayRow resist = Assert.Single(day.Resists());
        Assert.Equal(("A deepspore mushroom", 1, 1), (resist.Mob, resist.Landed, resist.Resisted));
        Assert.Equal(50, resist.ResistPercent, 3);
    }

    [Fact]
    public void The_scanner_reads_only_today_and_then_only_new_lines()
    {
        string path = Path.Combine(_dir, "eqlog_Stackprime_frostreaver.txt");
        string Stamp(DateTime when) => "[" + when.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture) + "] ";
        File.WriteAllText(path,
            Stamp(Day.AddHours(-1)) + "You gain experience! (50.000%)\n" +
            Stamp(Day.AddHours(10)) + "You gain experience! (1.000%)\n");
        var scanner = new DayScanner();

        scanner.Update(new[] { (path, "Stackprime") }, Day.AddHours(12));
        Assert.Equal(1.0, Assert.Single(scanner.Sessions(Day.AddHours(12))).XpPercent, 3);

        File.AppendAllText(path, Stamp(Day.AddHours(10).AddMinutes(1)) + "You gain experience! (2.000%)\n" + Stamp(Day.AddHours(10).AddMinutes(2)) + "You gain exp");
        scanner.Update(new[] { (path, "Stackprime") }, Day.AddHours(12));
        Assert.Equal(3.0, Assert.Single(scanner.Sessions(Day.AddHours(12))).XpPercent, 3);

        File.AppendAllText(path, "erience! (4.000%)\n");
        scanner.Update(new[] { (path, "Stackprime") }, Day.AddHours(12));
        SessionSummary s = Assert.Single(scanner.Sessions(Day.AddHours(12)));
        Assert.Equal(7.0, s.XpPercent, 3);
    }

    [Fact]
    public void Money_lines_are_read_in_copper()
    {
        Assert.Equal(1_185_229, DayAccumulator.CopperIn("You receive  1185 platinum 2 gold 2 silver 9 copper from Ward Taler for the contents of your bag."));
        Assert.True(DayAccumulator.IsMoneyLine("You receive 3 platinum from the corpse."));
        Assert.False(DayAccumulator.IsMoneyLine("You receive a character flag!"));
    }

    // ─────────────────────────── quiet logs and idle raiders ───────────────────────────

    [Fact]
    public void A_log_quiet_for_two_minutes_during_a_fight_is_reported_once()
    {
        var watch = new QuietLogWatch();
        (string, DateTime)[] lines = { ("Plethora", T0), ("Myriad", T0.AddMinutes(2)) };

        Assert.Empty(watch.Check(T0.AddMinutes(2.5), lines, fightInProgress: false));
        Assert.Equal(new[] { "Plethora" }, watch.Check(T0.AddMinutes(2.5), lines, fightInProgress: true));
        Assert.Empty(watch.Check(T0.AddMinutes(3), lines, fightInProgress: true));
        Assert.Empty(watch.Check(T0.AddMinutes(30), new[] { ("Myriad", T0.AddMinutes(2)) }, fightInProgress: true)); // long gone

        (string, DateTime)[] back = { ("Plethora", T0.AddMinutes(4)) };
        Assert.Equal(new[] { "Plethora" }, watch.Check(T0.AddMinutes(6.5), back, fightInProgress: true));
    }

    [Fact]
    public void Raid_members_with_no_damage_healing_or_casts_in_a_boss_kill_are_listed()
    {
        var raid = new RaidAttendance
        {
            Server = "frostreaver",
            Taken = Day.AddHours(20),
            Members = new[] { "Raideraa", "Raiderab", "Raiderac", "Raiderad", "Raiderae", "Raideraf", "Raiderag", "Raiderah", "Raiderai" }
                .Select(n => new RaidMember(1, n, 60, "Warrior", string.Empty)).ToList(),
        };
        ParticipantSnapshot[] attackers = new[] { "Raideraa", "Raiderab", "Raiderac", "Raiderad", "Raiderae", "Outsider" }
            .Select(n => new ParticipantSnapshot(n, 1000, 10, 10, 10, 100, 10, 0, 1)).ToArray();
        var fight = new EncounterSnapshot(1, "Emperor Ssraeshza", Day.AddHours(20.5), Day.AddHours(20.5).AddMinutes(3), 180, 6000, false,
            attackers, Healing: new[] { new HealingSnapshot("Raideraf", 5000, 0, 0, 3, 2000, 10, "Complete Heal", 0, 0) },
            Killed: true, PlayerCasts: new[] { new PlayerCastSnapshot(Day.AddHours(20.5), "Raiderag", "Mesmerize") })
        { Server = "frostreaver" };
        EncounterSnapshot trash = fight with { Id = 2, TargetName = "a trash mob", Participants = attackers.Take(3).ToArray() };

        IdleInFight row = Assert.Single(RaidIdle.Find(raid, new[] { fight, trash }));

        Assert.Equal("Emperor Ssraeshza", row.Boss);
        Assert.Equal(new[] { "Raiderah", "Raiderai" }, row.Idle);
    }

    // ─────────────────────────── pets and rampage ───────────────────────────

    private static void Feed(DpsTrackerService tracker, string self, params string[] lines)
    {
        foreach (string line in lines)
        {
            Assert.True(LogLineFormat.TryParseTimestamp(line, out DateTime ts));
            if (CombatLineParser.TryParse(line, ts, self, out CombatEvent e))
            {
                tracker.OnCombatEvent(e);
            }
        }
    }

    [Fact]
    public void A_merged_pet_keeps_its_own_sub_line_under_its_owner()
    {
        var roster = new PlayerRoster();
        roster.LearnPet("Koal", "Oodles");
        var tracker = new LiveDpsTracker(roster: roster);

        Feed(tracker, "Oodles",
            TestData.Line("You hit a ghoul for 100 points of damage.", "20:00:01"),
            TestData.Line("Koal slashes a ghoul for 18 points of damage.", "20:00:02"),
            TestData.Line("Koal slashes a ghoul for 22 points of damage.", "20:00:03"),
            TestData.Line("Koal tries to hit a ghoul, but misses!", "20:00:04"));

        ParticipantSnapshot owner = Assert.Single(Assert.Single(tracker.Snapshot.Active).Participants);
        Assert.Equal(("Oodles", 140L), (owner.Name, owner.TotalDamage));
        PetLine pet = Assert.Single(owner.PetRows);
        Assert.Equal(("Koal", 40L, 2, 1), (pet.Name, pet.Damage, pet.Hits, pet.Misses));
        Assert.Equal(66.67, pet.HitPercent, 2);
    }

    [Fact]
    public void Rampage_hits_and_misses_are_counted_for_each_target()
    {
        var tracker = new LiveDpsTracker();
        Feed(tracker, "Ream",
            TestData.Line("You hit Trooper Mykeor for 100 points of damage.", "20:00:01"),
            TestData.Line("Trooper Mykeor hits Ream for 207 points of damage. (Rampage)", "20:00:02"),
            TestData.Line("Trooper Mykeor tries to hit Ream, but misses! (Rampage)", "20:00:03"),
            TestData.Line("Trooper Mykeor hits YOU for 50 points of damage.", "20:00:04"));

        DamageTakenSnapshot ream = Assert.Single(Assert.Single(tracker.Snapshot.Active).TakenRows);
        Assert.Equal((2, 207L), (ream.Rampages, ream.RampageDamage));
        Assert.Equal(257, ream.TotalDamage);
    }

    // ─────────────────────────── character flags ───────────────────────────

    [Fact]
    public void Character_flags_are_kept_per_character()
    {
        var store = new ProgressStore();
        store.OnLine("Stackprime", "frostreaver", "You receive a character flag!", T0);
        store.OnLine("Stackprime", "frostreaver", "You have become better at 1H Blunt! (102)", T0.AddSeconds(5));

        CharacterFlag flag = Assert.Single(store.Flags("Stackprime", "frostreaver"));
        Assert.Equal("You receive a character flag!", flag.Text);
        Assert.Equal(T0, flag.Utc);
        Assert.Empty(store.Flags("Oodles", "frostreaver"));
    }
}
