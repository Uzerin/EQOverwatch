using EQCompanion.Core.Models;
using EQCompanion.Core.Services;
using EQCompanion.Core.Settings;
using EQCompanion.Core.Spells;
using Xunit;

namespace EQCompanion.Core.Tests;

public sealed class AbilityTrackerServiceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 23, 18, 0, 0, DateTimeKind.Utc);

    private readonly TriggerEngineService _engine = new();
    private readonly AbilityTrackerService _tracker;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    public AbilityTrackerServiceTests()
    {
        _tracker = new AbilityTrackerService(_engine);
        _tracker.SetMonitoredCharacters(new[] { "Stackprime", "Oodles" });
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static TrackedAbilitySetting Defensive(AbilityScope scope = AbilityScope.Everyone) => new()
    {
        Name = "Defensive Discipline", ActiveSeconds = 180, CooldownSeconds = 630, Scope = scope,
        ActiveOverlay = TimerOverlayCategory.Buffs, CooldownOverlay = TimerOverlayCategory.RaidTriggers,
    };

    private void Line(string message, DateTime at, string log = "Stackprime") => _tracker.OnLine(log, message, at);

    private IReadOnlyList<ActiveTimerSnapshot> Timers => _engine.Snapshot.Timers;

    [Fact]
    public void Using_an_ability_starts_an_active_bar_and_a_cooldown_bar_in_their_own_overlays()
    {
        _tracker.SetAbilities(new[] { Defensive() });

        Line("You activate Defensive Discipline.", T0);

        ActiveTimerSnapshot active = Assert.Single(Timers, t => t.Text.EndsWith("(active)"));
        Assert.Equal("Stackprime: Defensive Discipline (active)", active.Text);
        Assert.Equal(180, active.DurationSeconds);
        Assert.Equal(TimerOverlayCategory.Buffs, active.Category);

        ActiveTimerSnapshot cooldown = Assert.Single(Timers, t => t.Text.EndsWith("(cooldown)"));
        Assert.Equal("Stackprime: Defensive Discipline (cooldown)", cooldown.Text);
        Assert.Equal(630, cooldown.DurationSeconds);
        Assert.Equal(TimerOverlayCategory.RaidTriggers, cooldown.Category);
        Assert.Equal(2, _tracker.TimersStarted);
    }

    [Fact]
    public void A_zero_length_part_starts_no_bar()
    {
        TrackedAbilitySetting ability = Defensive();
        ability.ActiveSeconds = 0;
        _tracker.SetAbilities(new[] { ability });

        Line("You activate Defensive Discipline.", T0);

        Assert.Equal("Stackprime: Defensive Discipline (cooldown)", Assert.Single(Timers).Text);
    }

    [Fact]
    public void Everyone_scope_gives_every_raid_member_a_bar_of_their_own_that_others_never_reset()
    {
        _tracker.SetAbilities(new[] { Defensive() });

        Line("Regnier activates Defensive Discipline.", T0);
        Line("Fullfrontal activates Defensive Discipline.", T0.AddSeconds(60));

        Assert.Equal(4, Timers.Count);
        ActiveTimerSnapshot regnier = Assert.Single(Timers, t => t.Text == "Regnier: Defensive Discipline (active)");
        Assert.Equal(T0, regnier.StartedUtc); // Fullfrontal's use did not touch it
        Assert.Contains(Timers, t => t.Text == "Fullfrontal: Defensive Discipline (cooldown)");
    }

    [Fact]
    public void Using_it_again_restarts_only_that_users_bars()
    {
        _tracker.SetAbilities(new[] { Defensive() });
        Line("Regnier activates Defensive Discipline.", T0);
        Line("Fullfrontal activates Defensive Discipline.", T0.AddSeconds(5));

        Line("Regnier activates Defensive Discipline.", T0.AddMinutes(2)); // both users' bars are still running

        Assert.Equal(T0.AddMinutes(2), Assert.Single(Timers, t => t.Text == "Regnier: Defensive Discipline (active)").StartedUtc);
        Assert.Equal(T0.AddSeconds(5), Assert.Single(Timers, t => t.Text == "Fullfrontal: Defensive Discipline (active)").StartedUtc);
    }

    [Fact]
    public void My_characters_scope_ignores_other_players_but_not_my_own_characters()
    {
        _tracker.SetAbilities(new[] { Defensive(AbilityScope.MyCharacters) });

        Line("Regnier activates Defensive Discipline.", T0);
        Assert.Empty(Timers);

        Line("Oodles activates Defensive Discipline.", T0.AddSeconds(1), log: "Stackprime"); // another of mine, seen by Stackprime
        Line("You activate Defensive Discipline.", T0.AddSeconds(2), log: "Stackprime");

        Assert.Contains(Timers, t => t.Text == "Oodles: Defensive Discipline (active)");
        Assert.Contains(Timers, t => t.Text == "Stackprime: Defensive Discipline (active)");
        Assert.DoesNotContain(Timers, t => t.CharacterName == "Regnier");
    }

    [Fact]
    public void Scope_is_chosen_per_ability()
    {
        var trueshot = new TrackedAbilitySetting
        {
            Name = "Trueshot Discipline", ActiveSeconds = 120, CooldownSeconds = 4050, Scope = AbilityScope.MyCharacters,
        };
        _tracker.SetAbilities(new[] { Defensive(), trueshot });

        Line("Regnier activates Defensive Discipline.", T0);
        Line("Nicolita activates Trueshot Discipline.", T0);
        Line("Oodles activates Trueshot Discipline.", T0);

        Assert.Contains(Timers, t => t.Text == "Regnier: Defensive Discipline (active)");
        Assert.DoesNotContain(Timers, t => t.Text.StartsWith("Nicolita"));
        Assert.Contains(Timers, t => t.Text == "Oodles: Trueshot Discipline (cooldown)");
    }

    [Fact]
    public void The_same_use_seen_by_two_of_my_logs_is_one_use()
    {
        _tracker.SetAbilities(new[] { Defensive() });

        Line("You activate Defensive Discipline.", T0, log: "Stackprime");
        Line("Stackprime activates Defensive Discipline.", T0.AddMilliseconds(400), log: "Oodles");

        Assert.Equal(2, Timers.Count);
        Assert.Equal(2, _tracker.TimersStarted);
        Assert.All(Timers, t => Assert.Equal(T0, t.StartedUtc));
    }

    [Fact]
    public void Names_match_in_any_case_and_other_lines_are_ignored()
    {
        _tracker.SetAbilities(new[] { Defensive() });

        Line("You activate defensive discipline.", T0);
        Line("You activate Defensive Discipline Rk. II.", T0.AddSeconds(10));
        Line("a goblin activates Defensive Discipline.", T0.AddSeconds(10));
        Line("Regnier's pet activates Defensive Discipline.", T0.AddSeconds(10));
        Line("You slash a gnoll for 5 points of damage.", T0.AddSeconds(10));
        Line("You activate Defensive Discipline", T0.AddSeconds(10)); // no full stop

        Assert.Equal(2, Timers.Count);
        Assert.All(Timers, t => Assert.Equal("Stackprime", t.CharacterName));
    }

    [Fact]
    public void Disabled_abilities_and_abilities_with_no_duration_are_ignored()
    {
        TrackedAbilitySetting off = Defensive();
        off.IsEnabled = false;
        var empty = new TrackedAbilitySetting { Name = "Trueshot Discipline" };
        _tracker.SetAbilities(new[] { off, empty });

        Line("You activate Defensive Discipline.", T0);
        Line("You activate Trueshot Discipline.", T0);

        Assert.Empty(Timers);
        Assert.Equal(0, _tracker.AbilityCount);
    }

    // ───────────────────────── spell file and settings ─────────────────────────

    [Fact]
    public void The_spell_file_offers_abilities_with_their_recast_and_duration()
    {
        string path = SpellTestData.WriteFile(Path.Combine(_dir, "spells_us.txt"),
            SpellTestData.SplitRow(4499, "Defensive Discipline", 0, 11, 30, recastMs: 630000),
            SpellTestData.SplitRow(4500, "Defensive Discipline", 0, 11, 25, recastMs: 600000), // another rank: the slower one wins
            SpellTestData.SplitRow(88, "Harm Touch", 0, 0, 0),                                  // an AA: no recast in the file
            SpellTestData.SplitRow(200, "Quick Nuke", 0, 0, 0, recastMs: 3000));                // too fast to be an ability
        var service = new SpellDatabaseService();

        service.Load(path);

        AbilityInfo ability = Assert.Single(service.Database.Abilities);
        Assert.Equal("Defensive Discipline", ability.Name);
        Assert.Equal(630, ability.RecastSeconds);
        Assert.Equal(180, ability.ActiveSeconds);
        Assert.Equal(4499, service.Database.FindAbility("defensive discipline")!.SpellId);
        Assert.Null(service.Database.FindAbility("Harm Touch"));
    }

    [Fact]
    public void Saved_abilities_are_cleaned_up()
    {
        List<TrackedAbilitySetting> cleaned = AppSettingsService.NormalizeTrackedAbilities(new TrackedAbilitySetting?[]
        {
            new() { Name = "  Defensive Discipline ", ActiveSeconds = -5, CooldownSeconds = 9_999_999, Scope = (AbilityScope)9 },
            new() { Name = "defensive discipline", ActiveSeconds = 1 }, // duplicate name
            new() { Name = "   " },
            null,
        }!);

        TrackedAbilitySetting only = Assert.Single(cleaned);
        Assert.Equal("Defensive Discipline", only.Name);
        Assert.Equal(0, only.ActiveSeconds);
        Assert.Equal(TrackedAbilitySetting.MaxSeconds, only.CooldownSeconds);
        Assert.Equal(AbilityScope.Everyone, only.Scope);
    }
}
