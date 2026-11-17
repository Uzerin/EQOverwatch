using System.Globalization;
using System.IO.Compression;
using System.Text;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace EQOverwatch.Core.Tests;

/// <summary>Every character's log feeds the DPS parse; a line several logs saw is counted once.</summary>
public class MultiLogDpsTests(ITestOutputHelper output)
{
    private static readonly DateTime T0 = new(2026, 11, 16, 21, 18, 0);

    private static string Line(DateTime at, string message) =>
        "[" + at.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture) + "] " + message;

    // ───────────────────────────── the repeat filter ─────────────────────────────

    [Fact]
    public void The_same_line_in_two_logs_counts_once_even_a_second_apart()
    {
        var filter = new RepeatFilter();
        Assert.False(filter.IsRepeat("hit|552", T0, "Myriad"));
        Assert.True(filter.IsRepeat("hit|552", T0.AddSeconds(1), "Oodles"));
        Assert.True(filter.IsRepeat("hit|552", T0, "Stackprime"));
    }

    [Fact]
    public void Lines_too_far_apart_or_from_the_same_log_both_count()
    {
        var filter = new RepeatFilter();
        Assert.False(filter.IsRepeat("hit|552", T0, "Myriad"));
        Assert.False(filter.IsRepeat("hit|552", T0, "Myriad")); // two identical hits in one log are two hits
        Assert.False(filter.IsRepeat("hit|552", T0.AddSeconds(3), "Oodles")); // three seconds apart: another hit
        Assert.True(filter.IsRepeat("hit|552", T0, "Oodles"));
        Assert.True(filter.IsRepeat("hit|552", T0, "Stackprime"));
        Assert.True(filter.IsRepeat("hit|552", T0, "Stackprime"));
        Assert.False(filter.IsRepeat("hit|552", T0, "Stackprime")); // a third copy in this log that no other log had two of
    }

    [Fact]
    public void A_steady_run_of_identical_lines_one_second_off_is_counted_once_per_line()
    {
        // A damage shield hits for the same amount every two seconds; the second log stamps each line one second later.
        var filter = new RepeatFilter();
        int counted = 0;
        for (int i = 0; i < 20; i++)
        {
            counted += filter.IsRepeat("ds|5", T0.AddSeconds(2 * i), "Stackprime") ? 0 : 1;
            counted += filter.IsRepeat("ds|5", T0.AddSeconds(2 * i + 1), "Oodles") ? 0 : 1;
        }

        Assert.Equal(20, counted);
    }

    // ───────────────────────────── the tracker ─────────────────────────────

    /// <summary>Feeds lines to a tracker as the live pipeline does: each line tagged with its log's character.</summary>
    private static void Feed(LiveDpsTracker tracker, IEnumerable<(string Owner, string Line)> lines)
    {
        foreach ((string owner, string line) in lines)
        {
            Assert.True(LogLineFormat.TryParseTimestamp(line, out DateTime ts));
            tracker.OnLogOwner(owner);
            if (CombatLineParser.TryParse(line, ts, owner, out CombatEvent e))
            {
                tracker.OnCombatEvent(e);
            }
            else
            {
                tracker.OnLogClock(ts);
                tracker.OnNonCombatMessage(line.AsSpan(LogLineFormat.MessageOffset));
            }
        }
    }

    [Fact]
    public void A_hit_heal_cast_and_death_seen_in_two_logs_count_once_and_each_logs_own_lines_count()
    {
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Stackprime");
        tracker.AddKnownPlayer("Oodles");
        Feed(tracker, new[]
        {
            ("Stackprime", Line(T0, "You slash a gnome for 100 points of damage.")),
            ("Oodles", Line(T0, "Stackprime slashes a gnome for 100 points of damage.")),
            ("Oodles", Line(T0.AddSeconds(1), "You begin casting Complete Heal.")),
            ("Stackprime", Line(T0.AddSeconds(1), "Oodles begins casting Complete Heal.")),
            ("Oodles", Line(T0.AddSeconds(2), "You healed Stackprime for 638 (16830) hit points by Complete Heal.")),
            ("Stackprime", Line(T0.AddSeconds(3), "Oodles healed you for 638 (16830) hit points by Complete Heal.")),
            ("Oodles", Line(T0.AddSeconds(3), "You bash a gnome for 40 points of damage.")), // only Oodles' log has it
            ("Stackprime", Line(T0.AddSeconds(4), "You slash a gnome for 100 points of damage.")),
            ("Stackprime", Line(T0.AddSeconds(4), "You slash a gnome for 100 points of damage.")), // a second, identical hit
            ("Oodles", Line(T0.AddSeconds(5), "Stackprime slashes a gnome for 100 points of damage.")),
            ("Oodles", Line(T0.AddSeconds(5), "Stackprime slashes a gnome for 100 points of damage.")),
            ("Stackprime", Line(T0.AddSeconds(6), "A gnome has been slain by Stackprime!")),
            ("Oodles", Line(T0.AddSeconds(6), "A gnome has been slain by Stackprime!")),
        });

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Completed);
        Assert.True(fight.Killed);
        Assert.Equal(340, fight.TotalDamage);
        Assert.Equal(300, fight.Participants.Single(p => p.Name == "Stackprime").TotalDamage);
        Assert.Equal(40, fight.Participants.Single(p => p.Name == "Oodles").TotalDamage);
        Assert.Equal(638, fight.HealingRows.Single(h => h.Name == "Oodles").Effective);
        Assert.Single(fight.PlayerCastRows, c => c.Caster == "Oodles");
    }

    /// <summary>Last night's Emperor Ssraeshza attempt from five of the user's characters' logs, 21:15 to 21:25 (other players renamed).</summary>
    private static List<(string Owner, string Line)> EmpFight()
    {
        using var gzip = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Data", "emp-five-logs.log.gz")), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.Latin1);
        var lines = new List<(string, string)>();
        while (reader.ReadLine() is { } row)
        {
            int bar = row.IndexOf('|');
            lines.Add((row[..bar], row[(bar + 1)..]));
        }

        return lines;
    }

    /// <summary>Each log in time order (a stable sort keeps each log's own order), as the start-up catch-up reads them.</summary>
    private static IEnumerable<(string Owner, string Line)> InTimeOrder(IEnumerable<(string Owner, string Line)> lines) =>
        lines.Select(l => (l, Time: LogLineFormat.TryParseTimestamp(l.Line, out DateTime t) ? t : default))
            .OrderBy(x => x.Time)
            .Select(x => x.l);

    private static (Dictionary<string, long> ByPlayer, DateTime LastHit) EmpDamage(IEnumerable<(string Owner, string Line)> lines)
    {
        var tracker = new LiveDpsTracker();
        foreach (string own in new[] { "Stackprime", "Oodles", "Myriad", "Plethora", "Abundancei" })
        {
            tracker.AddKnownPlayer(own);
        }

        Feed(tracker, lines);
        tracker.OnLogClock(new DateTime(2026, 11, 16, 21, 40, 0)); // everything ends
        var byPlayer = new Dictionary<string, long>(StringComparer.Ordinal);
        DateTime last = default;
        foreach (EncounterSnapshot fight in tracker.Snapshot.Completed.Where(f => f.TargetName.Equals("Emperor Ssraeshza", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (ParticipantSnapshot p in fight.Participants)
            {
                byPlayer[p.Name] = byPlayer.GetValueOrDefault(p.Name) + p.TotalDamage;
            }

            if (fight.EndTime > last)
            {
                last = fight.EndTime;
            }
        }

        return (byPlayer, last);
    }

    [Fact]
    public void The_emp_fight_from_five_logs_keeps_going_after_the_main_log_dies_and_counts_every_hit_once()
    {
        List<(string Owner, string Line)> all = EmpFight();
        string[] owners = all.Select(l => l.Owner).Distinct().ToArray();
        Assert.Equal(5, owners.Length);

        (Dictionary<string, long> combined, DateTime combinedEnd) = EmpDamage(InTimeOrder(all));
        long combinedTotal = combined.Values.Sum();
        long sumOfSingles = 0;
        foreach (string owner in owners)
        {
            (Dictionary<string, long> single, DateTime singleEnd) = EmpDamage(all.Where(l => l.Owner == owner));
            sumOfSingles += single.Values.Sum();
            output.WriteLine($"{owner}: {single.Values.Sum():N0} until {singleEnd:HH:mm:ss}");

            // Nothing any one log saw is lost.
            foreach ((string player, long damage) in single)
            {
                Assert.True(combined.GetValueOrDefault(player) >= damage, $"{player}: {combined.GetValueOrDefault(player):N0} < {damage:N0} in {owner}'s log");
            }

            if (owner == "Stackprime")
            {
                Assert.True(combinedEnd > singleEnd, $"the combined fight ends {combinedEnd:HH:mm:ss}, Stackprime's at {singleEnd:HH:mm:ss}");
            }
        }

        output.WriteLine($"combined: {combinedTotal:N0} until {combinedEnd:HH:mm:ss}; the logs added up: {sumOfSingles:N0}");

        // The shared hits are not counted five times: far less than the logs added up.
        Assert.True(combinedTotal < sumOfSingles * 0.6, $"{combinedTotal:N0} vs {sumOfSingles:N0}");
    }

    // ───────────────────────────── reading several logs ─────────────────────────────

    [Fact]
    public async Task Several_logs_are_read_as_one_in_time_order_and_a_shared_hit_counts_once()
    {
        string dir = Path.Combine(Path.GetTempPath(), "eqo-multi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "eqlog_Stackprime_frostreaver.txt");
            string b = Path.Combine(dir, "eqlog_Oodles_frostreaver.txt");
            File.WriteAllLines(a, new[]
            {
                Line(T0, "You slash a gnome for 100 points of damage."),
                Line(T0.AddSeconds(2), "You slash a gnome for 100 points of damage."),
            });
            File.WriteAllLines(b, new[]
            {
                Line(T0.AddSeconds(1), "Stackprime slashes a gnome for 100 points of damage."), // a second late in this log
                Line(T0.AddSeconds(3), "You bash a gnome for 50 points of damage."),
                Line(T0.AddSeconds(4), "A gnome has been slain by Oodles!"),
            });

            var tracker = new HistoricalDpsTracker();
            HistoricalImportResult result = await new HistoricalLogImporter(tracker).ImportAsync(new[] { a, b });

            Assert.Equal(2, result.Logs);
            Assert.Equal("2 logs", result.CharacterName);
            EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Completed);
            Assert.True(fight.Killed);
            Assert.Equal(200, fight.Participants.Single(p => p.Name == "Stackprime").TotalDamage);
            Assert.Equal(50, fight.Participants.Single(p => p.Name == "Oodles").TotalDamage);
            Assert.Equal("frostreaver", fight.Server);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task With_a_start_time_only_the_part_wanted_is_read()
    {
        string dir = Path.Combine(Path.GetTempPath(), "eqo-seek-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "eqlog_Stackprime_frostreaver.txt");
            DateTime start = new(2026, 11, 1, 0, 0, 0);
            using (var writer = new StreamWriter(path, false, Encoding.Latin1))
            {
                for (int i = 0; i < 100_000; i++)
                {
                    writer.WriteLine(Line(start.AddSeconds(10 * i), $"Oodles tells the guild, 'line {i}'"));
                }
            }

            DateTime from = start.AddSeconds(10 * 90_000);
            var tracker = new HistoricalDpsTracker();
            HistoricalImportResult result = await new HistoricalLogImporter(tracker).ImportAsync(new[] { path }, from);

            Assert.True(result.LinesRead < 15_000, $"{result.LinesRead:N0} lines read");
            Assert.Equal(10_000, result.LinesQueued); // every line from the start time on, and none before it
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Log_seek_lands_on_a_line_start_before_the_time_and_close_to_it()
    {
        DateTime start = new(2026, 11, 1, 0, 0, 0);
        var text = new StringBuilder();
        var offsets = new List<long>();
        long at = 0;
        for (int i = 0; i < 50_000; i++)
        {
            offsets.Add(at);
            string line = Line(start.AddSeconds(i), "line " + i.ToString(CultureInfo.InvariantCulture)) + "\r\n";
            text.Append(line);
            at += line.Length;
        }

        using var stream = new MemoryStream(Encoding.Latin1.GetBytes(text.ToString()));
        long offset = LogSeek.FindOffset(stream, start.AddSeconds(40_000));

        int index = offsets.IndexOf(offset);
        Assert.True(index >= 0, "the offset is a line start");
        Assert.True(index <= 40_000, $"line {index} is past the time");
        Assert.True(offsets[40_000] - offset <= 300 * 1024, "close to the time");
        Assert.Equal(0, LogSeek.FindOffset(stream, start.AddDays(-1)));
    }
}
