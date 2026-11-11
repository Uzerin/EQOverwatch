using System.Threading.Channels;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Roster;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;
using Xunit;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

/// <summary>Players, levels, classes and pets are kept per server: a /who on one server never changes another server's character.</summary>
public class RosterServerTests
{
    private const string Giant = "a frost giant";
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private static void Line(PlayerRoster roster, string character, string server, string message) =>
        ((ITriggerLineSink)roster).OnLine(character, server, message, Now);

    [Fact]
    public void A_who_on_another_server_does_not_change_the_same_name_here()
    {
        var roster = new PlayerRoster();
        Line(roster, "Kaelith", "frostreaver", "[125 Warlord (Cleric)] Kaelith (Human)");
        Line(roster, "Kaelith", "newserver", "[12 Acolyte (Shaman)] Kaelith (Troll)");

        Assert.Equal(125, roster.GetLevel("Kaelith", "frostreaver"));
        Assert.Equal("Cleric", roster.GetClass("Kaelith", "Frostreaver")); // server capitals do not matter
        Assert.Equal(12, roster.GetLevel("Kaelith", "newserver"));
        Assert.Equal("Shaman", roster.GetClass("Kaelith", "newserver"));
        Assert.Equal(0, roster.GetLevel("Kaelith", "thirdserver"));
    }

    [Fact]
    public void The_most_recent_sighting_on_the_server_wins_and_no_server_means_the_latest_anywhere()
    {
        var roster = new PlayerRoster();
        Line(roster, "Kaelith", "newserver", "[12 Acolyte (Shaman)] Kaelith (Troll)");
        Line(roster, "Kaelith", "frostreaver", "[125 Warlord (Cleric)] Kaelith (Human)");
        Assert.Equal(125, roster.GetLevel("Kaelith"));
        Assert.Equal("frostreaver", roster.GetLatestServer("Kaelith"));

        Line(roster, "Kaelith", "newserver", "You have gained a level! Welcome to level 13!");
        Assert.Equal(13, roster.GetLevel("Kaelith", "newserver"));
        Assert.Equal(125, roster.GetLevel("Kaelith", "frostreaver"));
        Assert.Equal(13, roster.GetLevel("Kaelith"));
    }

    [Fact]
    public void Pets_belong_to_the_server_they_were_seen_on()
    {
        var roster = new PlayerRoster();
        Assert.True(roster.LearnPet("Gabnik", "Oodles", "frostreaver"));

        Assert.True(roster.IsPet("Gabnik", "frostreaver"));
        Assert.False(roster.IsPet("Gabnik", "newserver"));
        Assert.True(roster.TryGetOwner("Gabnik", "frostreaver", out string owner));
        Assert.Equal("Oodles", owner);
        Assert.False(roster.TryGetOwner("Gabnik", "newserver", out _));
        Assert.True(roster.IsVerifiedPlayer("Oodles", "frostreaver"));
        Assert.False(roster.IsVerifiedPlayer("Oodles", "newserver"));

        // On the other server the same name can be a player.
        Assert.True(roster.LearnPlayer("Gabnik", "Warrior", 50, "newserver"));
        Assert.Equal("WAR", roster.GetClassLabel("Gabnik", "newserver"));
        Assert.Equal(PlayerRoster.PetLabel, roster.GetClassLabel("Gabnik", "frostreaver"));
    }

    [Fact]
    public void Forgetting_on_one_server_keeps_the_other()
    {
        var roster = new PlayerRoster();
        roster.LearnPlayer("Kaelith", "Cleric", 125, "frostreaver");
        roster.LearnPlayer("Kaelith", "Shaman", 12, "newserver");

        Assert.True(roster.ForgetPlayer("Kaelith", "newserver"));
        Assert.Equal(125, roster.GetLevel("Kaelith", "frostreaver"));
        Assert.Equal(125, roster.GetLevel("Kaelith"));
        Assert.Equal(0, roster.GetLevel("Kaelith", "newserver"));
    }

    [Fact]
    public void Servers_round_trip_through_settings()
    {
        var roster = new PlayerRoster();
        roster.LearnPlayer("Kaelith", "Cleric", 125, "frostreaver");
        roster.LearnPlayer("Kaelith", "Shaman", 12, "newserver");
        roster.LearnPet("Gabnik", "Oodles", "frostreaver");

        var loaded = new PlayerRoster();
        loaded.Load(roster.ToSettings());

        Assert.Equal(125, loaded.GetLevel("Kaelith", "frostreaver"));
        Assert.Equal(12, loaded.GetLevel("Kaelith", "newserver"));
        Assert.Equal(12, loaded.GetLevel("Kaelith")); // newserver was seen last
        Assert.True(loaded.IsPet("Gabnik", "frostreaver"));
        Assert.False(loaded.IsPet("Gabnik", "newserver"));
    }

    [Fact]
    public void Old_entries_without_a_server_are_filed_under_one_and_keep_working_until_then()
    {
        var saved = new RosterSettings
        {
            Players = { new RosterPlayerEntry { Name = "Kaelith", Class = "Cleric", Level = 125 } },
            Pets = { new RosterPetEntry { Pet = "Gabnik", Owner = "Oodles" } },
        };
        var roster = new PlayerRoster();
        roster.Load(saved);
        Assert.Equal(125, roster.GetLevel("Kaelith", "frostreaver")); // an old entry answers for any server until it is filed

        Assert.True(roster.AssignLegacyServers(_ => "frostreaver"));
        Assert.Equal(125, roster.GetLevel("Kaelith", "frostreaver"));
        Assert.Equal(0, roster.GetLevel("Kaelith", "newserver"));
        Assert.True(roster.IsPet("Gabnik", "frostreaver"));
        Assert.False(roster.IsPet("Gabnik", "newserver"));
        Assert.All(roster.ToSettings().Players, p => Assert.Equal("frostreaver", p.Server));
    }

    [Fact]
    public void Lines_carry_their_server_from_the_log_through_the_pipeline()
    {
        var roster = new PlayerRoster();
        var sinks = new CompositeTriggerSink(new ITriggerLineSink[] { roster });
        Channel<LogEvent> channel = Channel.CreateUnbounded<LogEvent>();
        channel.Writer.TryWrite(new LogEvent("Kaelith", "[Mon Nov 09 12:00:00 2026] [125 Warlord (Cleric)] Kaelith (Human)", Now, LogRoute.Triggers, serverName: "frostreaver"));
        channel.Writer.TryWrite(new LogEvent("Kaelith", "[Mon Nov 09 12:00:01 2026] [12 Acolyte (Shaman)] Kaelith (Troll)", Now, LogRoute.Triggers, serverName: "newserver"));
        channel.Writer.Complete();

        LogEventProcessor.RunAsync(channel.Reader, sinks, null, null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(125, roster.GetLevel("Kaelith", "frostreaver"));
        Assert.Equal(12, roster.GetLevel("Kaelith", "newserver"));
    }

    [Fact]
    public void A_pet_on_another_server_is_not_merged_into_its_namesakes_owner()
    {
        var roster = new PlayerRoster();
        roster.LearnPet("Gabnik", "Oodles", "frostreaver");
        var tracker = new LiveDpsTracker(roster: roster);

        tracker.OnLogServer("newserver");
        tracker.OnCombatEvent(Hit("Gabnik", Giant, 100, "11:45:01"));
        tracker.OnCombatEvent(Hit("Oodles", Giant, 50, "11:45:02"));

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        Assert.Equal("newserver", fight.Server);
        Assert.Equal(new[] { "Gabnik", "Oodles" }, fight.Participants.Select(p => p.Name).OrderBy(n => n));

        var sameServer = new LiveDpsTracker(roster: roster);
        sameServer.OnLogServer("frostreaver");
        sameServer.OnCombatEvent(Hit("Gabnik", Giant, 100, "11:45:01"));
        Assert.Equal("Oodles", Assert.Single(Assert.Single(sameServer.Snapshot.Active).Participants).Name);
    }

    [Fact]
    public void Fight_classes_are_looked_up_on_the_fights_server()
    {
        var roster = new PlayerRoster();
        roster.LearnPlayer("Kaelith", "Cleric", 125, "frostreaver");
        roster.LearnPlayer("Kaelith", "Shaman", 12, "newserver");
        var tracker = new LiveDpsTracker(roster: roster);
        tracker.OnLogServer("frostreaver");
        tracker.OnCombatEvent(Hit("Kaelith", Giant, 100, "11:45:01"));

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active).WithClassesOnServer(roster.GetClassLabel);
        Assert.Equal("CLR", Assert.Single(fight.Participants).Class);
    }

    [Fact]
    public void Additional_characters_are_saved_with_their_servers_without_duplicates()
    {
        List<AdditionalCharacterSetting> clean = AppSettingsService.NormalizeCharacterServers(new[]
        {
            new AdditionalCharacterSetting { Name = " Thalra ", Server = "frostreaver" },
            new AdditionalCharacterSetting { Name = "thalra", Server = "Frostreaver" },
            new AdditionalCharacterSetting { Name = "Thalra", Server = "newserver" },
            new AdditionalCharacterSetting { Name = "", Server = "newserver" },
            null,
        });

        Assert.Equal(new[] { ("Thalra", "frostreaver"), ("Thalra", "newserver") }, clean.Select(c => (c.Name, c.Server)));
    }
}
