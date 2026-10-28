using EQOverwatch.Core.Export;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;
using Xunit;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

public sealed class EncounterExportTests
{
    private static EncounterSnapshot Fight(int attackers)
    {
        var tracker = new LiveDpsTracker();
        foreach (string second in new[] { "11:45:01", "11:45:05" })
        {
            for (int i = 0; i < attackers; i++)
            {
                string name = $"Player{i:00}";
                tracker.AddKnownPlayer(name);
                tracker.OnCombatEvent(Hit(name, "a frost giant", 1000 + (i * 100), second));
            }
        }

        return Assert.Single(tracker.Snapshot.Active);
    }

    [Fact]
    public void Chat_text_lists_the_top_twelve_ranked_by_damage()
    {
        EncounterSnapshot fight = Fight(15);

        string[] lines = EncounterExport.BuildChatText(fight).Split('\n');

        Assert.Equal(13, lines.Length); // header + 12
        Assert.StartsWith("a frost giant - ", lines[0]);
        Assert.StartsWith("1. Player14 ", lines[1]);
        Assert.StartsWith("12. Player03 ", lines[12]);
        Assert.Matches(@"^1\. Player14 [\d,]+ \([\d,]+\)$", lines[1]);
    }

    [Fact]
    public void The_picture_table_can_list_every_attacker_while_the_default_stays_at_twelve()
    {
        EncounterSnapshot fight = Fight(30);

        IReadOnlyList<EncounterExportRow> everyone = EncounterExport.BuildRows(fight, EncounterExport.AllAttackers);

        Assert.Equal(30, everyone.Count);
        Assert.Equal(Enumerable.Range(1, 30), everyone.Select(r => r.Rank));
        Assert.Equal("Player29", everyone[0].Name);
        Assert.Equal("Player00", everyone[29].Name);
        Assert.Equal(12, EncounterExport.BuildRows(fight).Count);
    }

    [Fact]
    public void Fewer_attackers_give_fewer_lines_and_the_format_is_culture_independent()
    {
        EncounterSnapshot fight = Fight(2);
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            string text = EncounterExport.BuildChatText(fight);

            Assert.Equal(3, text.Split('\n').Length);
            Assert.Contains("4,200", text); // 2 x (1000+1100) for the whole fight header: invariant thousands separator
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Durations_are_formatted_as_minutes_and_seconds()
    {
        Assert.Equal("0:42", EncounterExport.FormatDuration(42));
        Assert.Equal("1:05", EncounterExport.FormatDuration(65));
        Assert.Equal("1:05:09", EncounterExport.FormatDuration(3909));
    }

    [Fact]
    public void Top_participants_respects_the_requested_count()
    {
        EncounterSnapshot fight = Fight(5);

        Assert.Equal(3, EncounterExport.TopParticipants(fight, 3).Count);
        Assert.Empty(EncounterExport.TopParticipants(fight, 0));
    }
}
