using System.Globalization;
using EQOverwatch.Core.Experience;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Resists;
using EQOverwatch.Core.Search;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Spells;
using Xunit;
using static EQOverwatch.Core.Tests.SpellTestData;

namespace EQOverwatch.Core.Tests;

/// <summary>Automatic song bars, charm watch, the Experience tab, resists by type with raid sharing, and log search.</summary>
public sealed class SongsCharmXpResistSearchTests : IDisposable
{
    private const int Selo = 717;
    private const int Warsong = 3374;
    private const int Charm = 300;
    private const int Mark = 2507;
    private const int Lure = 1769;
    private const int Judgment = 2508;
    private const int Elixir = 1288;

    private static readonly DateTime T0 = new(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));
    private readonly SpellDatabaseService _spells = new();
    private readonly TriggerEngineService _engine = new();

    public SongsCharmXpResistSearchTests()
    {
        string spells = WriteFile(Path.Combine(_dir, "spells_us.txt"),
            SplitRow(Selo, "Selo's Accelerando", 0, 1, 50, bardLevel: 5),             // formula 1: half the level in ticks, at most 50
            SplitRow(Warsong, "Warsong of the Vah Shir", 0, 11, 3, bardLevel: 60),
            SplitRow(Charm, "Charm", 2500, 11, 10, goodEffect: 0, resistType: 1, effects: "1|22|0|0|0|100"),
            SplitRow(Mark, "Mark of Retribution", 3000, 11, 5, goodEffect: 0, resistType: 1),
            SplitRow(Lure, "Lure of Ice", 4500, 0, 0, goodEffect: 0, resistType: 3),
            SplitRow(Judgment, "Judgment", 3000, 0, 0, goodEffect: 0, resistType: 1),
            SplitRow(Elixir, "Celestial Elixir", 3000, 11, 10));
        WriteFile(Path.Combine(_dir, "spells_us_str.txt"),
            "#SPELLINDEX^CASTERMETXT^CASTEROTHERTXT^CASTEDMETXT^CASTEDOTHERTXT^SPELLGONE^",
            "717^^^You feel your speed increase.^'s speed increases.^Your speed returns to normal.^",
            "3374^^^Your body pulses with the spirit of the Vah Shir.^ pulses with the spirit of the Vah Shir.^The spirit leaves you.^",
            "300^^^You have been charmed.^ has been charmed.^You are no longer charmed.^",
            "2507^^^Your skin gleams with a dull red aura.^'s skin gleams with a dull red aura.^The dull red aura fades.^",
            "1769^^^You succumb to the lure of ice.^ succumbs to the lure of ice.^^",
            "2508^^^You are struck by the judgment of the gods.^ has been struck by the judgment of the gods.^^",
            "1288^^^You feel the elixir.^ feels the elixir.^The elixir fades.^");
        _spells.Load(spells);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private IReadOnlyList<ActiveTimerSnapshot> Timers => _engine.Snapshot.Timers;

    // ─────────────────────────── songs ───────────────────────────

    [Fact]
    public void Your_song_goes_to_the_songs_overlay_without_being_on_any_list_and_lasts_its_own_time()
    {
        var songs = new SongTrackerService(_spells, _engine);
        songs.SetLevelProvider(_ => 0);

        songs.OnLine("Oodles", "You begin singing Selo's Accelerando.", T0);

        ActiveTimerSnapshot bar = Assert.Single(Timers);
        Assert.Equal("Oodles: Selo's Accelerando", bar.Text);
        Assert.Equal(TimerOverlayCategory.Songs, bar.Category);
        Assert.Equal(30 * 6, bar.DurationSeconds); // the file's caster level (60): 30 ticks
    }

    [Fact]
    public void Your_song_uses_your_characters_level_when_known()
    {
        var songs = new SongTrackerService(_spells, _engine);
        songs.SetLevelProvider(name => name == "Oodles" ? 20 : 0);

        songs.OnLine("Oodles", "You begin singing Selo's Accelerando.", T0);

        Assert.Equal(10 * 6, Assert.Single(Timers).DurationSeconds); // level 20: 10 ticks
    }

    [Fact]
    public void Other_bards_show_only_when_switched_on_and_once_for_several_logs()
    {
        var songs = new SongTrackerService(_spells, _engine);
        songs.SetOwnCharacterProbe(name => name is "Oodles" or "Ream");
        songs.SetActivityProbe((_, _) => true);

        songs.OnLine("Ream", "Trick begins singing Warsong of the Vah Shir.", T0);
        Assert.Empty(Timers);

        songs.ShowAllBards = true;
        songs.OnLine("Ream", "Trick begins singing Warsong of the Vah Shir.", T0);
        songs.OnLine("Stackprime", "Trick begins to sing a song. <Warsong of the Vah Shir>", T0.AddSeconds(1));
        songs.OnLine("Ream", "Oodles begins singing Warsong of the Vah Shir.", T0); // your own bard: its own log reports it
        songs.OnLine("Ream", "A Shissar bard begins singing Warsong of the Vah Shir.", T0); // a mob

        ActiveTimerSnapshot bar = Assert.Single(Timers);
        Assert.Equal("Trick: Warsong of the Vah Shir", bar.Text);
        Assert.Equal("Ream", bar.CharacterName);
        Assert.Equal(TimerOverlayCategory.Songs, bar.Category);
    }

    [Fact]
    public void A_song_on_the_spells_list_no_longer_starts_a_second_bar_from_the_buff_tracker()
    {
        var buffs = new BuffTrackerService(_spells, _engine);
        buffs.SetTrackedSpells(new[] { Selo });
        var songs = new SongTrackerService(_spells, _engine);

        buffs.OnLine("Oodles", "You begin singing Selo's Accelerando.", T0);
        songs.OnLine("Oodles", "You begin singing Selo's Accelerando.", T0);
        buffs.OnLine("Oodles", "Oodles's speed increases.", T0);

        Assert.Single(Timers);
    }

    // ─────────────────────────── charm ───────────────────────────

    [Fact]
    public void Your_charm_gets_a_bar_and_its_break_a_red_banner_and_a_spoken_alert()
    {
        var charm = new CharmWatchService(_spells, _engine);
        var spoken = new List<string>();
        _engine.TriggerFired += e => spoken.Add(e.AudioText ?? string.Empty);

        charm.OnLine("Ream", "You begin casting Charm.", T0);
        charm.OnLine("Ream", "a rock golem has been charmed.", T0.AddSeconds(2));

        ActiveTimerSnapshot bar = Assert.Single(Timers);
        Assert.Equal("Ream: Charm, a rock golem", bar.Text);
        Assert.Equal(TimerOverlayCategory.Debuffs, bar.Category);
        Assert.Equal(60, bar.DurationSeconds);

        charm.OnLine("Ream", "Your Celestial Elixir spell has worn off of Stackprime.", T0.AddSeconds(5)); // not a charm
        Assert.Single(Timers);

        charm.OnLine("Ream", "Your Charm spell has worn off of a rock golem.", T0.AddSeconds(30));

        ActiveTimerSnapshot banner = Assert.Single(Timers);
        Assert.Equal(TimerKind.TextAlert, banner.Kind);
        Assert.Equal("CHARM BROKE: Ream, a rock golem", banner.Text);
        Assert.Equal(CharmWatchService.BrokeBannerSeconds, banner.DurationSeconds);
        Assert.Contains("Charm broke", spoken);
    }

    [Fact]
    public void A_charm_landing_long_after_the_cast_or_from_someone_else_is_ignored()
    {
        var charm = new CharmWatchService(_spells, _engine);

        charm.OnLine("Ream", "a rock golem has been charmed.", T0); // somebody else's charm
        charm.OnLine("Ream", "You begin casting Charm.", T0);
        charm.OnLine("Ream", "a skeleton has been charmed.", T0.AddSeconds(30)); // far too late

        Assert.Empty(Timers);

        charm.IsEnabled = false;
        charm.OnLine("Ream", "You begin casting Charm.", T0);
        charm.OnLine("Ream", "a skeleton has been charmed.", T0.AddSeconds(1));
        Assert.Empty(Timers);
    }

    // ─────────────────────────── experience ───────────────────────────

    [Fact]
    public void Experience_lines_add_up_per_character_with_rates_and_time_to_level()
    {
        var xp = new ExperienceTracker(T0);
        xp.SetStartingPoint("Ream", 59, 50);

        xp.OnLine("Ream", "You gain party experience (with a bonus)! (5.2%)", T0.AddMinutes(10));
        xp.OnLine("Ream", "You gained raid experience (with a bonus)! (4.800%)", T0.AddMinutes(20));
        xp.OnLine("Ream", "You gain experience!", T0.AddMinutes(30)); // a kill without a percentage
        xp.OnLine("Stackprime", "You have gained an ability point!  You now have 3 ability points.", T0.AddMinutes(40));
        xp.OnLine("Ream", "Fullfrontal experiences visions of grandeur.", T0.AddMinutes(40));

        IReadOnlyList<ExperienceRow> rows = xp.Rows(T0.AddHours(1));
        ExperienceRow ream = rows.Single(r => r.Character == "Ream");
        Assert.Equal(59, ream.Level);
        Assert.Equal(10.0, ream.XpPercent, 3);
        Assert.Equal(10.0, ream.XpPerHour, 3);
        Assert.Equal(60.0, ream.ProgressPercent!.Value, 3);
        Assert.Equal(4.0, ream.NextLevelIn!.Value.TotalHours, 3);
        Assert.Equal((3, 1, 1, 1), (ream.Kills, ream.PartyKills, ream.RaidKills, ream.SoloKills));

        ExperienceRow stack = rows.Single(r => r.Character == "Stackprime");
        Assert.Equal(1, stack.AaGained);
        Assert.Null(stack.NextLevelIn);
    }

    [Fact]
    public void A_level_up_counts_and_starts_the_level_from_zero()
    {
        var xp = new ExperienceTracker(T0);
        xp.OnLine("Ream", "You gain experience! (60.5%)", T0.AddMinutes(5));
        xp.OnLine("Ream", "You have gained a level! Welcome to level 60!", T0.AddMinutes(6));
        xp.OnLine("Ream", "You gain experience! (10%)", T0.AddMinutes(7));

        ExperienceRow ream = Assert.Single(xp.Rows(T0.AddMinutes(30)));
        Assert.Equal(60, ream.Level);
        Assert.Equal(1, ream.LevelsGained);
        Assert.Equal(10.0, ream.ProgressPercent!.Value, 3);

        xp.Reset(T0.AddMinutes(31));
        Assert.Empty(xp.Rows(T0.AddMinutes(32)));
        xp.OnLine("Ream", "You gain experience! (1%)", T0.AddMinutes(33));
        Assert.Equal(11.0, Assert.Single(xp.Rows(T0.AddMinutes(34))).ProgressPercent!.Value, 3);
    }

    [Fact]
    public void The_starting_point_comes_from_the_last_level_up_in_the_log()
    {
        string log = WriteFile(Path.Combine(_dir, "eqlog_Ream_frostreaver.txt"),
            "[Fri Aug 14 23:27:49 2026] You have gained a level! Welcome to level 58!",
            "[Fri Aug 14 23:28:00 2026] You gain party experience (with a bonus)! (40.5%)",
            "[Fri Aug 14 23:34:15 2026] You have gained a level! Welcome to level 59!",
            "[Fri Aug 14 23:35:00 2026] You gain party experience (with a bonus)! (2.25%)",
            "[Fri Aug 14 23:36:00 2026] You gained raid experience! (1.000%)");

        (int level, double progress) = ExperienceTracker.ScanStartingPoint(log)!.Value;

        Assert.Equal(59, level);
        Assert.Equal(3.25, progress, 3);
    }

    // ─────────────────────────── resists ───────────────────────────

    [Fact]
    public void The_spell_file_gives_each_spell_its_resist_type()
    {
        Assert.Equal(SpellResist.Magic, _spells.Database.FindDefinitionByName("Mark of Retribution")!.ResistType);
        Assert.Equal(SpellResist.Cold, _spells.Database.FindDefinitionByName("lure of ice")!.ResistType);
        Assert.Equal(3000, _spells.Database.FindDefinitionByName("Judgment")!.CastTimeMs);
        Assert.Equal(0, _spells.Database.FindDefinitionByName("Celestial Elixir")!.ResistType);
    }

    [Fact]
    public void Your_spells_on_a_mob_count_as_landed_or_resisted_by_resist_type()
    {
        var store = new MobResistStore();
        var observer = new ResistObserver(_spells, store);
        observer.SetIsPlayer(name => name == "Abundancei");

        observer.Observe("Ream", "You begin casting Mark of Retribution.", T0);
        observer.Observe("Ream", "a horror guard's skin gleams with a dull red aura.", T0.AddSeconds(3));
        observer.Observe("Ream", "You begin casting Mark of Retribution.", T0.AddSeconds(10));
        observer.Observe("Ream", "A horror guard resisted your Mark of Retribution!", T0.AddSeconds(13));
        observer.Observe("Stackprime", "You begin casting Lure of Ice.", T0);
        observer.Observe("Stackprime", "a horror guard succumbs to the lure of ice.", T0.AddSeconds(5));
        observer.Observe("Ream", "a horror guard's skin gleams with a dull red aura.", T0.AddSeconds(40)); // nobody of yours cast it
        observer.Observe("Ream", "You begin casting Judgment.", T0.AddSeconds(50));
        observer.Observe("Ream", "Abundancei resisted your Judgment!", T0.AddSeconds(53)); // a player, not a mob

        IReadOnlyDictionary<int, ResistCount> own = store.Own("A HORROR GUARD");
        Assert.Equal(new ResistCount(1, 1), own[SpellResist.Magic]);
        Assert.Equal(new ResistCount(1, 0), own[SpellResist.Cold]);
        Assert.Equal(50.0, own[SpellResist.Magic].Percent, 3);
        Assert.Empty(store.Own("Abundancei"));
    }

    [Fact]
    public void Songs_and_spells_without_landing_text_are_not_counted_so_resists_are_not_overstated()
    {
        var store = new MobResistStore();
        var observer = new ResistObserver(_spells, store);

        observer.Observe("Oodles", "You begin singing Selo's Accelerando.", T0);
        observer.Observe("Oodles", "A horror guard resisted your Selo's Accelerando!", T0.AddSeconds(1));
        observer.Observe("Oodles", "A horror guard resisted your Unknown Nuke!", T0.AddSeconds(2));

        Assert.Empty(store.Own("A horror guard"));
    }

    [Fact]
    public void Raid_figures_add_shares_and_leave_out_an_outlier_per_type()
    {
        var store = new MobResistStore();
        for (int i = 0; i < 8; i++)
        {
            store.AddOwn("Xin Thall Centien", "Ream", SpellResist.Fire, resisted: i < 2, T0); // 25%
        }

        store.ApplyShare(new ResistShare("Xin Thall Centien", "Elias", T0, new() { [SpellResist.Fire] = new ResistCount(39, 11), [SpellResist.Magic] = new ResistCount(98, 2) }));
        store.ApplyShare(new ResistShare("Xin Thall Centien", "Linksded", T0, new() { [SpellResist.Fire] = new ResistCount(0, 5000) }));

        MobResistReport mine = store.Report("Xin Thall Centien", raid: false);
        Assert.Equal(new ResistCount(6, 2), mine.ByType[SpellResist.Fire]);
        Assert.False(mine.ByType.ContainsKey(SpellResist.Magic));

        MobResistReport raid = store.Report("Xin Thall Centien", raid: true);
        Assert.Equal(new ResistCount(45, 13), raid.ByType[SpellResist.Fire]); // Linksded's 100% is left out
        Assert.Equal(new ResistCount(98, 2), raid.ByType[SpellResist.Magic]);
        Assert.Contains(SpellResist.Fire, raid.Contributors.Single(c => c.Player == "Linksded").LeftOut);
        Assert.Empty(raid.Contributors.Single(c => c.Player == "Elias").LeftOut);
    }

    [Fact]
    public void A_players_newer_share_replaces_their_older_one()
    {
        var store = new MobResistStore();
        store.ApplyShare(new ResistShare("Trakanon", "Trick", T0, new() { [SpellResist.Poison] = new ResistCount(10, 10) }));
        store.ApplyShare(new ResistShare("Trakanon", "Trick", T0.AddHours(1), new() { [SpellResist.Poison] = new ResistCount(20, 0) }));
        store.ApplyShare(new ResistShare("Trakanon", "Trick", T0.AddMinutes(5), new() { [SpellResist.Poison] = new ResistCount(1, 1) })); // older: ignored

        Assert.Equal(new ResistCount(20, 0), store.Report("Trakanon", raid: true).ByType[SpellResist.Poison]);
        Assert.Equal(1, store.ShareCount("Trakanon"));
    }

    [Fact]
    public void Share_tokens_round_trip_and_are_read_only_from_raid_and_guild_chat_of_other_players()
    {
        string token = ResistShareCodec.Encode("Xin Thall Centien", new Dictionary<int, ResistCount>
        {
            [SpellResist.Magic] = new ResistCount(180, 3), [SpellResist.Fire] = new ResistCount(22, 6),
        })!;
        Assert.Equal("{EQO:R1:Xin Thall Centien:M180-3:F22-6}", token);

        var store = new MobResistStore();
        var tracker = new ResistTrackerService(_spells, store);
        tracker.SetOwnCharacterProbe(name => name == "Oodles");

        tracker.OnLine("Ream", $"Trick tells the raid,  '{token}'", T0);
        tracker.OnLine("Ream", $"Elias tells the guild, 'here {token}'", T0);
        tracker.OnLine("Ream", $"Oodles tells the raid,  '{token}'", T0);             // your own character
        tracker.OnLine("Ream", $"Linksded says out of character, '{token}'", T0);   // not raid or guild
        tracker.OnLine("Ream", $"Linksded tells the raid,  '{{EQO:R1:Xin Thall Centien:M1-x}}'", T0); // damaged

        MobResistReport raid = store.Report("Xin Thall Centien", raid: true);
        Assert.Equal(new[] { "Elias", "Trick" }, raid.Contributors.Select(c => c.Player).OrderBy(p => p));
        Assert.Equal(new ResistCount(360, 6), raid.ByType[SpellResist.Magic]);
    }

    [Fact]
    public void The_store_survives_a_restart()
    {
        string path = Path.Combine(_dir, "mob-resists.json");
        var store = new MobResistStore();
        store.SetStoragePath(path, T0);
        store.AddOwn("Trakanon", "Ream", SpellResist.Poison, resisted: true, T0);
        store.ApplyShare(new ResistShare("Trakanon", "Trick", T0, new() { [SpellResist.Poison] = new ResistCount(3, 1) }));
        store.AddCovered("Ream", new DateTime(2026, 11, 1, 20, 0, 0), new DateTime(2026, 11, 1, 22, 0, 0));
        store.Flush();

        var again = new MobResistStore();
        again.SetStoragePath(path, T0.AddDays(1));
        Assert.Equal(new ResistCount(0, 1), again.Own("Trakanon")[SpellResist.Poison]);
        Assert.Equal(1, again.ShareCount("Trakanon"));
        Assert.True(again.IsCovered("Ream", new DateTime(2026, 11, 1, 21, 0, 0)));

        var later = new MobResistStore();
        later.SetStoragePath(path, T0.AddDays(91));
        Assert.Equal(0, later.ShareCount("Trakanon")); // shares older than 90 days are dropped
    }

    [Fact]
    public async Task Reading_a_log_in_counts_its_resists_once()
    {
        string log = WriteFile(Path.Combine(_dir, "eqlog_Ream_frostreaver.txt"),
            "[Sun Nov 01 20:00:00 2026] You begin casting Mark of Retribution.",
            "[Sun Nov 01 20:00:03 2026] a horror guard's skin gleams with a dull red aura.",
            "[Sun Nov 01 20:00:10 2026] You begin casting Mark of Retribution.",
            "[Sun Nov 01 20:00:13 2026] A horror guard resisted your Mark of Retribution!");

        await using var pipeline = new EQOverwatch.Core.Pipeline.CompanionPipeline();
        pipeline.Spells.Load(Path.Combine(_dir, "spells_us.txt"));

        await pipeline.ImportHistoricalAsync(log);
        Assert.Equal(new ResistCount(1, 1), pipeline.ResistStore.Own("a horror guard")[SpellResist.Magic]);

        await pipeline.ImportHistoricalAsync(log);
        Assert.Equal(new ResistCount(1, 1), pipeline.ResistStore.Own("a horror guard")[SpellResist.Magic]);
    }

    // ─────────────────────────── log search ───────────────────────────

    [Fact]
    public void The_query_splits_into_words_and_quoted_phrases()
    {
        Assert.Equal(new[] { "spawn window", "sebilis" }, LogSearchService.ParseQuery("\"spawn window\" sebilis"));
        Assert.Empty(LogSearchService.ParseQuery("   "));
    }

    [Theory]
    [InlineData("Trick tells you, 'hi'", ChatChannel.Tells)]
    [InlineData("You told Trick, 'hi'", ChatChannel.Tells)]
    [InlineData("Trick tells the guild, 'hi'", ChatChannel.Guild)]
    [InlineData("Trick tells the raid,  'hi'", ChatChannel.Raid)]
    [InlineData("Trick tells the group, 'hi'", ChatChannel.Group)]
    [InlineData("Trick says, 'hi'", ChatChannel.Say)]
    [InlineData("Trick says out of character, 'hi'", ChatChannel.OocShoutAuction)]
    [InlineData("Trick auctions, 'WTS sword'", ChatChannel.OocShoutAuction)]
    [InlineData("Trick tells general:1, 'hi'", ChatChannel.ChatChannels)]
    [InlineData("A temple guard hits YOU for 50 points of damage.", ChatChannel.Other)]
    public void Lines_are_sorted_into_channels(string message, ChatChannel expected) =>
        Assert.Equal(expected, LogSearchService.Classify(message));

    [Fact]
    public void Search_finds_matching_chat_lines_from_a_date_newest_first()
    {
        var lines = new List<string>();
        var start = new DateTime(2026, 9, 1, 12, 0, 0);
        for (int i = 0; i < 20000; i++)
        {
            DateTime t = start.AddMinutes(i * 5);
            lines.Add("[" + t.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture) + "] " +
                (i % 1000 == 0 ? $"Trick tells the guild, 'trakanon spawn in sebilis {i}'" : "You gain experience!"));
        }

        string log = WriteFile(Path.Combine(_dir, "eqlog_Ream_frostreaver.txt"), lines.ToArray());
        DateTime from = start.AddMinutes(10000 * 5);

        LogSearchResult result = LogSearchService.Search(new[] { ("Ream", log) }, "SPAWN sebilis", ChatChannel.Guild | ChatChannel.Raid, from);

        Assert.Equal(new[] { 19000, 18000, 17000, 16000, 15000, 14000, 13000, 12000, 11000, 10000 },
            result.Hits.Select(h => int.Parse(h.Message[(h.Message.LastIndexOf(' ') + 1)..^1], CultureInfo.InvariantCulture)));
        Assert.All(result.Hits, h => Assert.Equal(ChatChannel.Guild, h.Channel));

        Assert.Empty(LogSearchService.Search(new[] { ("Ream", log) }, "sebilis", ChatChannel.Tells, null).Hits);
        Assert.True(LogSearchService.Search(new[] { ("Ream", log) }, "sebilis", ChatChannel.All, null, maxHits: 3).HitLimit);
    }
}
