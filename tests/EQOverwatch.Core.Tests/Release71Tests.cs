using EQOverwatch.Core.Chat;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Progress;
using EQOverwatch.Core.Services;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class Release71Tests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 22, 0, 0, DateTimeKind.Utc);

    private static CombatEvent Parse(string message, string self, string time = "20:00:01")
    {
        string line = TestData.Line(message, time);
        Assert.True(LogLineFormat.TryParseTimestamp(line, out DateTime ts));
        Assert.True(CombatLineParser.TryParse(line, ts, self, out CombatEvent e), message);
        return e;
    }

    [Fact]
    public void Floating_text_takes_each_number_from_its_own_characters_log()
    {
        var feed = new FloatingCombatFeed(name => name is "Stackprime" or "Myriad");
        void Combat(string owner, string message)
        {
            feed.OnLogOwner(owner);
            CombatEvent e = Parse(message, owner);
            feed.OnCombatEvent(in e);
        }

        Combat("Stackprime", "You slash a frost giant for 142 points of damage. (Critical)");
        Combat("Myriad", "Stackprime slashes a frost giant for 142 points of damage."); // the same hit in another log: not again
        Combat("Myriad", "You shoot a frost giant for 88 points of damage.");
        Combat("Stackprime", "A frost giant hits YOU for 300 points of damage.");
        feed.OnLogOwner("Stackprime");
        feed.OnNonCombatMessage("Ream healed you for 1250 (8415) hit points by Complete Heal. (Critical)");

        IReadOnlyList<FloatingNumber> numbers = feed.TakeAll();
        Assert.Equal(new[]
        {
            new FloatingNumber(FloatingPanel.Dealt, "Stackprime", 142, FloatingStyle.Melee, true),
            new FloatingNumber(FloatingPanel.Dealt, "Myriad", 88, FloatingStyle.Ranged, false),
            new FloatingNumber(FloatingPanel.Taken, "Stackprime", 300, FloatingStyle.Taken, false),
            new FloatingNumber(FloatingPanel.Healed, "Stackprime", 1250, FloatingStyle.Heal, true),
        }, numbers);
        Assert.Empty(feed.TakeAll());
    }

    [Fact]
    public void Dots_without_a_caster_count_on_bosses_and_on_players()
    {
        CombatEvent tick = Parse("Gnorm has taken 3 damage by Spreading Crud.", "Ream");
        Assert.Equal((CombatLineParser.UnknownDotSource, "Gnorm", 3, DamageKind.DamageOverTime), (tick.Source, tick.Target, tick.Amount, tick.DamageKind));
        CombatEvent own = Parse("You have taken 31 damage from Boiling Blood by a chromadrac.", "Ream");
        Assert.Equal(("a chromadrac", "Ream"), (own.Source, own.Target));

        var tracker = new LiveDpsTracker();
        void Feed(string message, string time)
        {
            CombatEvent e = Parse(message, "Ream", time);
            tracker.OnCombatEvent(in e);
        }

        Feed("You hit Emperor Ssraeshza for 100 points of damage.", "20:00:01");
        Feed("Emperor Ssraeshza has taken 250 damage by Waves of the Deep Sea.", "20:00:02");
        Feed("Emperor Ssraeshza hits Gnorm for 40 points of damage.", "20:00:03");
        Feed("Gnorm has taken 3 damage by Spreading Crud.", "20:00:04");

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        Assert.Equal(250, fight.Participants.Single(p => p.Name == CombatLineParser.UnknownDotSource).TotalDamage);
        DamageTakenSnapshot gnorm = fight.TakenRows.Single(t => t.Name == "Gnorm");
        Assert.Equal((43L, 1, 3L), (gnorm.TotalDamage, gnorm.DotTicks, gnorm.DotDamage));
    }

    [Fact]
    public void Chat_lines_are_sorted_by_kind_and_counted_once()
    {
        Assert.Equal(new ChatLine(T0, "Ream", ChatChannel.Raid, string.Empty, "Raideraa", "go go", false),
            ChatLog.Parse("Ream", "Raideraa tells the raid, 'go go'", T0));
        Assert.Equal(ChatChannel.Ooc, ChatLog.Parse("Ream", "Raideraa says out of character, 'hi'", T0)!.Channel);
        Assert.Equal((ChatChannel.Channel, "General:1"), (ChatLog.Parse("Ream", "Raideraa tells General:1, 'lfg'", T0)!.Channel, ChatLog.Parse("Ream", "Raideraa tells General:1, 'lfg'", T0)!.ChannelName));
        ChatLine tell = ChatLog.Parse("Ream", "Raideraa told you, 'inv pls'", T0)!;
        Assert.Equal((ChatChannel.Tell, "to Ream"), (tell.Channel, tell.ChannelName));
        ChatLine mine = ChatLog.Parse("Ream", "You say to your guild, 'grats'", T0)!;
        Assert.Equal((ChatChannel.Guild, "Ream", true), (mine.Channel, mine.Speaker, mine.IsYou));
        Assert.Null(ChatLog.Parse("Ream", "Merchant Kwein told you, 'I'll give you 1 silver'", T0)); // an NPC
        Assert.Null(ChatLog.Parse("Ream", "You have entered The Maiden's Eye.", T0));

        var chat = new ChatLog();
        chat.OnLine("Ream", "Raideraa tells the raid, 'go go'", T0);
        chat.OnLine("Stackprime", "Raideraa tells the raid, 'go go'", T0.AddSeconds(1)); // the same line in another log
        chat.OnLine("Ream", "Raideraa auctions, 'WTS Peridot'", T0.AddSeconds(2));
        Assert.Equal(new[] { ChatChannel.Raid, ChatChannel.Auction }, chat.All().Select(l => l.Channel));
    }

    [Fact]
    public void Legacy_bonus_and_birthdate_are_read()
    {
        Assert.Equal(100, ProgressStore.LegacyBonus("Your legacy server experience bonus for reaching the maximum level has increased to 100%!"));
        Assert.Equal(new DateTime(2026, 5, 27, 12, 22, 26), ProgressStore.Birthdate("Stackprime's birthdate: Wednesday, May 27, 2026 12:22:26."));

        var store = new ProgressStore();
        store.OnLine("Stackprime", "frostreaver", "Your legacy server experience bonus for reaching the maximum level has increased to 60%!", T0);
        store.OnLine("Stackprime", "frostreaver", "Stackprime's birthdate: Wednesday, May 27, 2026 12:22:26.", T0);
        Assert.Equal((60, new DateTime(2026, 5, 27, 12, 22, 26)), store.Legacy("Stackprime", "frostreaver"));
    }
}
