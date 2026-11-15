using System.Globalization;
using EQOverwatch.Core.Progress;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>Skill-ups, tradeskill combines, faction and the tell inbox, from live lines and log history.</summary>
public sealed class ProgressAndTellsTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqprogress-" + Guid.NewGuid().ToString("N"));

    public ProgressAndTellsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static void Line(ProgressStore store, string message, int second, string character = "Ream") =>
        store.OnLine(character, "frostreaver", message, T0.AddSeconds(second));

    [Fact]
    public void Skill_ups_set_each_skill_and_list_them_a_to_z()
    {
        var store = new ProgressStore(T0.AddHours(-1));
        Line(store, "You have become better at Offense! (211)", 0);
        Line(store, "You have become better at Offense! (212)", 30);
        Line(store, "You have become better at Abjuration! (224)", 40);
        Line(store, "You have become better at Offense! (5)", 50, "Myriad"); // another character

        IReadOnlyList<SkillProgress> skills = store.Skills("Ream", "Frostreaver", T0.AddMinutes(5));
        Assert.Equal(new[] { "Abjuration", "Offense" }, skills.Select(s => s.Skill));
        Assert.Equal(212, skills[1].Value);
        Assert.Equal(2, skills[1].GainedToday);
        Assert.Equal(T0.AddSeconds(30), skills[1].LastUtc);
        Assert.Equal(5, store.Skills("Myriad", "frostreaver", T0).Single().Value);
    }

    [Fact]
    public void Combines_count_made_and_failed_per_recipe()
    {
        var store = new ProgressStore();
        Line(store, "You lacked the skills to fashion Metal Bits.", 0);
        Line(store, "You lacked the skills to fashion Metal Bits.", 3);
        Line(store, "You have fashioned the items together to create something new: Metal Bits.", 6);
        Line(store, "You have fashioned the items together to create something new: Ring of the Shissar.", 9);

        IReadOnlyList<TradeskillProgress> recipes = store.Tradeskills("Ream", "frostreaver");
        Assert.Equal(new[] { "Metal Bits", "Ring of the Shissar" }, recipes.Select(r => r.Recipe));
        Assert.Equal((1, 2, 3), (recipes[0].Made, recipes[0].Failed, recipes[0].Combines));
        Assert.Equal(33.3, Math.Round(recipes[0].SuccessPercent, 1));
    }

    [Fact]
    public void Faction_hits_add_up_with_the_usual_hit_the_mob_and_the_limit()
    {
        var store = new ProgressStore(T0.AddSeconds(100));
        store.SetPlayerProbe(name => name == "Petream");
        Line(store, "Orc pawn has been slain by Jannder!", 0);
        Line(store, "Your faction standing with Crushbone Orcs has been adjusted by -2.", 0);
        Line(store, "Your faction standing with Crushbone Orcs has been adjusted by -2.", 50); // no kill just before
        Line(store, "You have slain an orc centurion!", 200);
        Line(store, "Your faction standing with Crushbone Orcs has been adjusted by -5.", 201);
        Line(store, "Orc pawn has been slain by Jannder!", 300);
        Line(store, "Petream has been slain by an orc centurion!", 300); // a pet's death is not the kill
        Line(store, "Your faction standing with Crushbone Orcs has been adjusted by -2.", 300);
        Line(store, "Your faction standing with Crushbone Orcs could not possibly get any worse.", 400);

        FactionProgress orcs = store.Factions("Ream", "frostreaver").Single();
        Assert.Equal(("Crushbone Orcs", -11L, 4), (orcs.Faction, orcs.AllTime, orcs.Hits));
        Assert.Equal(-7, orcs.Session); // since the session started (second 100)
        Assert.Equal(-2, orcs.UsualHit);
        Assert.Equal("Orc pawn", orcs.ComesFrom); // a name without an article keeps its capital
        Assert.Equal(-1, orcs.Limit);
    }

    [Fact]
    public void Reading_a_logs_history_replaces_what_it_gave_and_is_saved()
    {
        string log = Path.Combine(_dir, "eqlog_Ream_frostreaver.txt");
        File.WriteAllLines(log, new[]
        {
            Stamp(T0) + "You have become better at Meditate! (234)",
            Stamp(T0.AddSeconds(5)) + "You have become better at Meditate! (235)",
            Stamp(T0.AddSeconds(9)) + "Jannder tells you, 'need a rez?'",
            Stamp(T0.AddSeconds(9)) + "Some other line",
        });

        string path = Path.Combine(_dir, "progress.json");
        var store = new ProgressStore();
        store.SetStoragePath(path);
        Line(store, "You have become better at Meditate! (200)", -999); // counted live before the history was read
        ProgressStore.LogScan scan = store.BeginScan("Ream", "frostreaver");
        long? last = LogHistoryReader.Read(log, ProgressStore.Wanted, scan.Line);
        scan.Commit(last is not null);
        Assert.True(store.IsScanned("Ream", "frostreaver"));
        Assert.Equal(235, store.Skills("Ream", "frostreaver", T0).Single().Value);

        store.Flush();
        var again = new ProgressStore();
        again.SetStoragePath(path);
        Assert.True(again.IsScanned("Ream", "frostreaver"));
        Assert.Equal(235, again.Skills("Ream", "frostreaver", T0).Single().Value);
    }

    [Fact]
    public void Skill_caps_come_from_skillcaps_txt_by_class_skill_and_level()
    {
        var caps = new SkillCaps();
        caps.AddLine("2^31^60^325^0^");
        caps.AddLine("4^33^60^252^0^");
        caps.AddLine("junk");

        Assert.Equal(325, caps.Cap("Cleric", "Meditate", 60));
        Assert.Equal(325, caps.Cap("CLR", "meditate", 60));
        Assert.Equal(252, caps.Cap("Ranger", "Offense", 60));
        Assert.Equal(0, caps.Cap("Cleric", "Meditate", 59));
        Assert.Equal(0, caps.Cap("", "Meditate", 60));
        Assert.Equal(0, SkillCaps.Load(Path.Combine(_dir, "missing.txt")).Count);
    }

    // ───────────────────────── tells ─────────────────────────

    private static void Tell(TellInbox inbox, string message, int second, string character = "Stackprime") =>
        inbox.OnLine(character, "frostreaver", message, DateTime.UtcNow.AddSeconds(second - 3600));

    [Fact]
    public void Player_tells_are_kept_until_answered_and_npcs_and_your_own_characters_are_left_out()
    {
        var inbox = new TellInbox();
        inbox.SetOwnCharacterProbe(name => name == "Oodles");
        Tell(inbox, "Mommiessubby tells you, 'can you buy me enduring breath'", 0);
        Tell(inbox, "Kelanor tells you, 'lfg?'", 10, "Reamii");
        Tell(inbox, "Kendall Copperhold told you, 'Welcome to my bank!'", 20);
        Tell(inbox, "Petream told you, 'Attacking a vis ghoul knight Master.'", 21);
        Tell(inbox, "Oodles tells you, 'inc'", 22);

        Assert.Equal(new[] { "Kelanor", "Mommiessubby" }, inbox.All().Select(t => t.From));
        Assert.Equal(2, inbox.UnansweredCount());

        Tell(inbox, "You told Mommiessubby, 'sure'", 30, "Reamii"); // a different character: still waiting
        Assert.Equal(2, inbox.UnansweredCount());
        Tell(inbox, "You told mommiessubby, 'sure'", 31);
        Assert.True(inbox.All().Single(t => t.From == "Mommiessubby").Answered);
        Assert.Equal(1, inbox.UnansweredCount());

        inbox.MarkAllRead();
        Assert.Equal(0, inbox.UnansweredCount());
    }

    [Fact]
    public void Old_tells_are_dropped_after_the_days_chosen_and_history_older_than_half_a_day_counts_as_seen()
    {
        DateTime now = DateTime.UtcNow;
        string log = Path.Combine(_dir, "eqlog_Ream_frostreaver.txt");
        File.WriteAllLines(log, new[]
        {
            Stamp(now.AddDays(-40)) + "Basill tells you, 'old'",
            Stamp(now.AddDays(-2)) + "Taladir tells you, 'yesterday-ish'",
            Stamp(now.AddHours(-1)) + "Roen tells you, 'just now'",
        });

        var inbox = new TellInbox { KeepDays = 30 };
        TellInbox.LogScan scan = inbox.BeginScan("Ream", "frostreaver");
        scan.Commit(LogHistoryReader.Read(log, TellInbox.Wanted, scan.Line) is not null);

        Assert.Equal(new[] { "Roen", "Taladir" }, inbox.All().Select(t => t.From));
        Assert.Equal(new[] { true, false }, inbox.All().Select(t => t.IsUnanswered));

        inbox.KeepDays = 1;
        Assert.Equal("Roen", inbox.All().Single().From);

        string path = Path.Combine(_dir, "tells.json");
        inbox.SetStoragePath(path);
        inbox.MarkAllRead();
        inbox.Flush();
        var again = new TellInbox();
        again.SetStoragePath(path);
        Assert.True(again.IsScanned("Ream", "frostreaver"));
        Assert.Equal("Roen", again.All().Single().From);
        Assert.Equal(0, again.UnansweredCount());
    }

    private static string Stamp(DateTime utc) =>
        utc.ToLocalTime().ToString("[ddd MMM dd HH:mm:ss yyyy] ", CultureInfo.InvariantCulture);
}
