using System.IO;
using EQOverwatch.App.Services;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Spells;
using EQOverwatch.Core.Theming;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    private const string Giant = "a frost giant";

    private static readonly string[] SampleLog =
    {
        "[Wed Oct 21 11:45:01 2026] You slash a frost giant for 142 points of damage.",
        "[Wed Oct 21 11:45:02 2026] Stackprime crushes a frost giant for 210 points of damage.",
        "[Wed Oct 21 11:45:03 2026] Oodles hit a frost giant for 450 points of non-melee damage.",
        "[Wed Oct 21 11:45:04 2026] Ream begins to cast a spell. <Complete Heal>",
        "[Wed Oct 21 11:45:05 2026] Your protective skin fades.",
        "[Wed Oct 21 11:45:15 2026] a frost giant has been slain by Stackprime!",
    };

    private static CombatEvent Hit(string source, string target, int amount, int second) =>
        new(new DateTime(2026, 10, 21, 11, 45, second), CombatEventKind.Damage, source, target, amount, DamageKind.Melee, "hit");

    private static CombatEvent Miss(string source, string target, int second) =>
        new(new DateTime(2026, 10, 21, 11, 45, second), CombatEventKind.Miss, source, target, 0, DamageKind.None, "hit");

    // ───────────────────────────── fakes ─────────────────────────────

    private sealed class FakeDialogs : IDialogService
    {
        public string[]? LogFiles { get; set; }
        public string? ImportFile { get; set; }
        public string? WavFile { get; set; }
        public string? SpellsFile { get; set; }
        public int ImportPickCount { get; private set; }

        public string[]? PickLogFiles() => LogFiles;

        public string? PickImportLogFile()
        {
            ImportPickCount++;
            return ImportFile;
        }

        public string? PickWavFile() => WavFile;

        public string? PickSpellsFile() => SpellsFile;

        public string? EqFolder { get; set; }

        public string? PickEqFolder() => EqFolder;
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public List<string> Texts { get; } = new();
        public List<(EncounterSnapshot Encounter, string Footer, int TopCount)> Images { get; } = new();

        public void SetText(string text) => Texts.Add(text);

        public void SetEncounterImage(EncounterSnapshot encounter, ThemePalette palette, string footer, int topCount) =>
            Images.Add((encounter, footer, topCount));
    }

    private sealed class FakeOverlayHost : IOverlayHost
    {
        public Dictionary<OverlayKind, OverlaySettings> State { get; } = OverlayKindExtensions.All.ToDictionary(
            kind => kind,
            kind => kind.Category() is { } category ? OverlaySettings.ForCategory(category) : OverlaySettings.DefaultDps);

        public List<string> Calls { get; } = new();
        public int CloseAllCalls { get; private set; }

        public event EventHandler? PlacementChanged;

        public OverlaySettings Capture(OverlayKind kind) => State[kind];

        public void Apply(OverlayKind kind, OverlaySettings settings)
        {
            State[kind] = settings;
            Calls.Add($"apply:{kind}");
        }

        public void SetLocked(bool locked)
        {
            foreach (OverlayKind kind in State.Keys.ToList())
            {
                State[kind] = State[kind] with { IsLocked = locked };
            }

            Calls.Add($"lock:{locked}");
            PlacementChanged?.Invoke(this, EventArgs.Empty); // the real windows raise this too
        }

        public void SetVisible(OverlayKind kind, bool visible)
        {
            State[kind] = State[kind] with { IsVisible = visible };
            Calls.Add($"visible:{kind}:{visible}");
            PlacementChanged?.Invoke(this, EventArgs.Empty);
        }

        public void CloseAll() => CloseAllCalls++;

        public void RaisePlacementChanged() => PlacementChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeTheme : IThemeService
    {
        public List<ThemePalette> Applied { get; } = new();

        public ThemePalette Last => Applied[^1];

        public void Apply(ThemePalette palette) => Applied.Add(palette);
    }

    private sealed class FakeUpdateChecker : EQOverwatch.Core.Updates.IUpdateChecker
    {
        public EQOverwatch.Core.Updates.UpdateCheckResult Result { get; set; } = new(EQOverwatch.Core.Updates.UpdateCheckStatus.UpToDate, "v1.0.0");
        public int Calls { get; private set; }
        public string? LastCurrentVersion { get; private set; }

        public Task<EQOverwatch.Core.Updates.UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastCurrentVersion = currentVersion;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeAudio : IAudioControl
    {
        public bool IsEnabled { get; set; } = true;
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

        public DateTime Now { get; set; } = new DateTime(2026, 10, 26, 12, 0, 0);

        public Harness(Action<AppSettings, string>? configure = null)
        {
            Directory.CreateDirectory(_dir);
            var settings = new AppSettings();
            configure?.Invoke(settings, _dir);

            Service = new AppSettingsService(SettingsPath, TimeSpan.FromMilliseconds(30));
            Pipeline = new CompanionPipeline(TimeSpan.FromSeconds(settings.EncounterTimeoutSeconds));
            Vm = new MainViewModel(Pipeline, Service, settings, Overlays, Audio, Dialogs, Theme, Clipboard, () => Now, Updates, url => OpenedUrls.Add(url));
        }

        public FakeUpdateChecker Updates { get; } = new();
        public List<string> OpenedUrls { get; } = new();
        public FakeDialogs Dialogs { get; } = new();
        public FakeOverlayHost Overlays { get; } = new();
        public FakeAudio Audio { get; } = new();
        public FakeTheme Theme { get; } = new();
        public FakeClipboard Clipboard { get; } = new();
        public AppSettingsService Service { get; }
        public CompanionPipeline Pipeline { get; }
        public MainViewModel Vm { get; }

        public string SettingsPath => Path.Combine(_dir, "settings.json");

        public string SpellsPath => Path.Combine(_dir, "spells_us.txt");

        /// <summary>
        /// Writes a small unified-layout spells_us.txt: Spirit of Wolf (100, 15:00), Selo's Accelerando (101, 3:00),
        /// Short Buff (103, formula 1: 3:00 at level 60, 1:00 at level 20) and an instant heal that must be skipped.
        /// </summary>
        public static string WriteSpellFile(string path)
        {
            static string Row(int id, string name, string you, string other, string fades, int cast, int formula, int ticks)
            {
                string[] c = Enumerable.Repeat("0", 30).ToArray();
                c[0] = id.ToString();
                c[1] = name;
                c[6] = you;
                c[7] = other;
                c[8] = fades;
                c[13] = cast.ToString();
                c[16] = formula.ToString();
                c[17] = ticks.ToString();
                return string.Join('^', c);
            }

            File.WriteAllText(path, string.Join("\n",
                "# spells",
                Row(100, "Spirit of Wolf", "You feel quick as a wolf.", " looks quick.", "Your speed returns to normal.", 1500, 11, 150),
                Row(101, "Selo's Accelerando", "You feel your pace quicken.", " moves faster.", "pace returns to normal.", 0, 11, 30),
                Row(103, "Short Buff", "You feel brief power.", " glows briefly.", "Your power fades.", 3000, 1, 100),
                Row(104, "Instant Heal", "You feel better.", " feels better.", "", 2000, 0, 0)) + "\n");
            return path;
        }

        /// <summary>Creates a log file (default: empty) named like EverQuest's and returns its path.</summary>
        public string CreateLog(string character, params string[] lines)
        {
            string path = Path.Combine(_dir, $"eqlog_{character}_Frostreaver.txt");
            File.WriteAllText(path, lines.Length == 0 ? string.Empty : string.Join("\n", lines) + "\n");
            return path;
        }

        /// <summary>Forces the debounced save and reads the file back.</summary>
        public AppSettings Saved()
        {
            Service.Flush();
            return new AppSettingsService(SettingsPath).Load();
        }

        public void Dispose()
        {
            Vm.AutoImportTask.GetAwaiter().GetResult();
            Vm.SessionStartTask.GetAwaiter().GetResult();
            Vm.UpdateCheckTask.GetAwaiter().GetResult();
            Vm.Shutdown(); // idempotent
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ───────────────────────────── restore ─────────────────────────────

    [Fact]
    public void Restores_logs_triggers_timeout_mute_and_overlay_state_from_settings()
    {
        string tempDir = Path.GetTempPath();
        using var h = new Harness((s, _) =>
        {
            s.WatchedLogs.Add(new WatchedLogSettings
            {
                FilePath = Path.Combine(tempDir, "eqlog_Stackprime_Frostreaver.txt"), CharacterName = "Stackprime",
                IsTriggerEnabled = true, IsPrimaryDps = true,
            });
            s.WatchedLogs.Add(new WatchedLogSettings
            {
                FilePath = Path.Combine(tempDir, "eqlog_Oodles_Frostreaver.txt"), CharacterName = "Oodles",
                IsTriggerEnabled = false,
            });
            s.Triggers.Add(new TriggerRule
            {
                Name = "Skin", Pattern = @"^Your protective skin fades\.$", AlertTextTemplate = "${Character}: Skin faded",
            });
            s.EncounterTimeoutSeconds = 15;
            s.IsAudioMuted = true;
            s.BuffsOverlay = new OverlaySettings { Left = 10, Top = 20, Width = 300, Height = 200, IsVisible = false, IsLocked = true };
            s.DpsOverlay = new OverlaySettings { Left = 400, Top = 30, Width = 280, Height = 220, IsVisible = true, IsLocked = true };
            s.DebuffsOverlay = s.DebuffsOverlay with { IsLocked = true };
            s.SongsOverlay = s.SongsOverlay with { IsLocked = true };
            s.RaidTriggersOverlay = s.RaidTriggersOverlay with { IsLocked = true };
            s.ChChainOverlay = s.ChChainOverlay with { IsLocked = true };
        });

        Assert.Equal(2, h.Vm.Logs.Count);
        Assert.Equal("Stackprime", h.Vm.Logs.PrimaryDpsLog!.CharacterName);
        Assert.False(h.Vm.Logs[1].IsTriggerEnabled);

        TriggerRuleViewModel rule = Assert.Single(h.Vm.Rules);
        Assert.Same(rule, h.Vm.SelectedRule);
        h.Pipeline.Triggers.OnLine("Stackprime", "Your protective skin fades.", DateTime.UtcNow);
        Assert.Equal("Stackprime: Skin faded", Assert.Single(h.Pipeline.Triggers.Snapshot.Timers).Text);

        Assert.Equal(15, h.Vm.EncounterTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(15), h.Pipeline.EncounterTimeout);

        Assert.True(h.Vm.IsAudioMuted);
        Assert.False(h.Audio.IsEnabled);

        Assert.Equal(10, h.Overlays.State[OverlayKind.Buffs].Left);
        Assert.False(h.Vm.IsBuffsOverlayVisible);
        Assert.True(h.Vm.IsDpsOverlayVisible);
        Assert.True(h.Vm.AreOverlaysLocked);
        Assert.DoesNotContain(h.Overlays.Calls, c => c.StartsWith("lock:", StringComparison.Ordinal)); // restore must not re-apply
        Assert.False(File.Exists(h.SettingsPath)); // and restoring must not trigger a save
    }

    [Fact]
    public void Watched_characters_from_settings_are_known_players()
    {
        using var h = new Harness((s, _) => s.WatchedLogs.Add(new WatchedLogSettings
        {
            FilePath = Path.Combine(Path.GetTempPath(), "eqlog_Stackprime_Frostreaver.txt"),
        }));

        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", "Tormax", 100, 1));

        Assert.Equal("Tormax", Assert.Single(h.Pipeline.LiveDps.Snapshot.Active).TargetName);
    }

    // ───────────────────────────── logs ─────────────────────────────

    [Fact]
    public void Adding_a_log_registers_the_character_makes_the_first_one_primary_and_saves()
    {
        using var h = new Harness();
        string first = h.CreateLog("Stackprime");
        string second = h.CreateLog("Oodles");

        h.Dialogs.LogFiles = new[] { first, second };
        h.Vm.AddLogCommand.Execute(null);

        Assert.Equal(2, h.Vm.Logs.Count);
        Assert.True(h.Vm.Logs[0].IsPrimaryDps);
        Assert.False(h.Vm.Logs[1].IsPrimaryDps);

        // Both characters are known players, so hitting an unknown single-word boss is tracked right away.
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Oodles", "Tormax", 100, 1));
        Assert.Single(h.Pipeline.LiveDps.Snapshot.Active);

        AppSettings saved = h.Saved();
        Assert.Equal(new[] { "Stackprime", "Oodles" }, saved.WatchedLogs.Select(l => l.CharacterName));
        Assert.Equal(Path.GetFullPath(first), saved.WatchedLogs[0].FilePath);
        Assert.True(saved.WatchedLogs[0].IsPrimaryDps);
    }

    [Fact]
    public void Cancelling_the_file_dialog_changes_nothing()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = null;

        h.Vm.AddLogCommand.Execute(null);

        Assert.Empty(h.Vm.Logs);
    }

    [Fact]
    public void Primary_dps_is_exclusive_and_toggles_are_saved()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Stackprime"), h.CreateLog("Oodles") };
        h.Vm.AddLogCommand.Execute(null);

        h.Vm.Logs[1].IsPrimaryDps = true;
        h.Vm.Logs[0].IsTriggerEnabled = false;

        Assert.False(h.Vm.Logs[0].IsPrimaryDps);
        AppSettings saved = h.Saved();
        Assert.False(saved.WatchedLogs[0].IsPrimaryDps);
        Assert.True(saved.WatchedLogs[1].IsPrimaryDps);
        Assert.False(saved.WatchedLogs[0].IsTriggerEnabled);
    }

    [Fact]
    public void Removing_a_log_stops_watching_it_and_saves()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Stackprime"), h.CreateLog("Oodles") };
        h.Vm.AddLogCommand.Execute(null);
        WatchedLogConfig oodles = h.Vm.Logs[1];

        h.Vm.RemoveLogCommand.Execute(oodles);

        Assert.Equal("Stackprime", Assert.Single(h.Vm.Logs).CharacterName);
        Assert.Equal("Stackprime", Assert.Single(h.Saved().WatchedLogs).CharacterName);

        // A removed log no longer saves property changes.
        oodles.IsTriggerEnabled = false;
        Assert.Single(h.Saved().WatchedLogs);
    }

    [Fact]
    public async Task Live_monitoring_runs_once_the_app_has_started()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Stackprime") };
        h.Vm.AddLogCommand.Execute(null);

        await h.Vm.StartSessionAsync();

        Assert.True(h.Pipeline.IsLiveRunning);
        Assert.True(h.Vm.IsLiveRunning);
        Assert.Contains("1 log", h.Vm.LiveStatusText);
        Assert.Contains("Stackprime", h.Vm.LiveStatusText);
    }

    // ───────────────────────────── triggers ─────────────────────────────

    [Fact]
    public void Editing_a_rule_updates_the_engine_immediately_and_saves()
    {
        using var h = new Harness();

        h.Vm.AddRuleCommand.Execute(null);
        TriggerRuleViewModel rule = h.Vm.SelectedRule!;
        Assert.Equal("Trigger 1", rule.Name);
        Assert.NotNull(rule.Error); // empty pattern

        rule.Pattern = @"^hello (?<who>\w+)$";
        rule.AlertTextTemplate = "${who}!";
        Assert.Null(rule.Error);

        h.Pipeline.Triggers.OnLine("Stackprime", "hello Ream", DateTime.UtcNow);
        Assert.Equal("Ream!", Assert.Single(h.Pipeline.Triggers.Snapshot.Timers).Text);

        rule.Pattern = "(unclosed";
        Assert.NotNull(rule.Error);
        Assert.True(rule.HasError);

        rule.Pattern = @"^hello (?<who>\w+)$";
        Assert.False(rule.HasError);

        TriggerRule saved = Assert.Single(h.Saved().Triggers);
        Assert.Equal(rule.Id, saved.Id);
        Assert.Equal(@"^hello (?<who>\w+)$", saved.Pattern);
        Assert.Equal("${who}!", saved.AlertTextTemplate);
    }

    [Fact]
    public void Plain_text_rules_match_literally()
    {
        using var h = new Harness();
        h.Vm.AddRuleCommand.Execute(null);
        TriggerRuleViewModel rule = h.Vm.SelectedRule!;

        rule.UseRegex = false;
        rule.Pattern = "a.b(c)";

        h.Pipeline.Triggers.OnLine("Stackprime", "xx a.b(c) yy", DateTime.UtcNow);
        Assert.Single(h.Pipeline.Triggers.Snapshot.Timers);

        h.Pipeline.Triggers.ClearTimers();
        h.Pipeline.Triggers.OnLine("Stackprime", "xx aXb(c) yy", DateTime.UtcNow);
        Assert.Empty(h.Pipeline.Triggers.Snapshot.Timers);
    }

    [Fact]
    public void Removing_a_rule_stops_it_firing_and_selects_a_neighbour()
    {
        using var h = new Harness();
        h.Vm.AddRuleCommand.Execute(null);
        h.Vm.SelectedRule!.Pattern = "one";
        h.Vm.AddRuleCommand.Execute(null);
        h.Vm.SelectedRule!.Pattern = "two";

        h.Vm.RemoveRuleCommand.Execute(null);

        Assert.Equal("one", Assert.Single(h.Vm.Rules).Pattern);
        Assert.Same(h.Vm.Rules[0], h.Vm.SelectedRule);
        h.Pipeline.Triggers.OnLine("Stackprime", "two", DateTime.UtcNow);
        Assert.Empty(h.Pipeline.Triggers.Snapshot.Timers);
        Assert.Single(h.Saved().Triggers);
    }

    [Fact]
    public void Test_fire_reports_captures_and_fires_the_timer_for_real()
    {
        using var h = new Harness();
        h.Vm.AddRuleCommand.Execute(null);
        TriggerRuleViewModel rule = h.Vm.SelectedRule!;
        rule.Pattern = @"^(?<caster>\w+) begins to cast a spell\. <Complete Heal>$";
        rule.AlertTextTemplate = string.Empty;
        rule.TimerDurationSeconds = 10;
        rule.TimerLabelTemplate = "${caster}: Complete Heal";

        // A line copied straight out of a log, timestamp and all.
        h.Vm.TestLine = SampleLog[3];
        h.Vm.FireSelectedRuleCommand.Execute(null);

        Assert.Contains("caster=Ream", h.Vm.TestResultText);
        ActiveTimerSnapshot timer = Assert.Single(h.Pipeline.Triggers.Snapshot.Timers);
        Assert.Equal(TimerKind.Countdown, timer.Kind);
        Assert.Equal("Ream: Complete Heal", timer.Text);
        Assert.Equal(10, timer.DurationSeconds);
    }

    [Fact]
    public void Test_fire_explains_misses_and_invalid_patterns_and_works_on_disabled_rules()
    {
        using var h = new Harness();
        h.Vm.AddRuleCommand.Execute(null);
        TriggerRuleViewModel rule = h.Vm.SelectedRule!;
        rule.Pattern = "^hello$";
        rule.IsEnabled = false;

        h.Vm.TestLine = "goodbye";
        h.Vm.FireSelectedRuleCommand.Execute(null);
        Assert.StartsWith("No match", h.Vm.TestResultText);

        h.Vm.TestLine = string.Empty;
        h.Vm.FireSelectedRuleCommand.Execute(null);
        Assert.Contains("Enter a sample log line", h.Vm.TestResultText);

        h.Vm.TestLine = "hello";
        h.Vm.FireSelectedRuleCommand.Execute(null); // disabled rules can still be test-fired
        Assert.StartsWith("Matched", h.Vm.TestResultText);
        Assert.Single(h.Pipeline.Triggers.Snapshot.Timers);

        rule.Pattern = "(unclosed";
        h.Vm.FireSelectedRuleCommand.Execute(null);
        Assert.StartsWith("Invalid pattern", h.Vm.TestResultText);
    }

    [Fact]
    public void The_wav_picker_fills_in_the_selected_rule()
    {
        using var h = new Harness();
        h.Vm.AddRuleCommand.Execute(null);
        h.Dialogs.WavFile = @"C:\sounds\ding.wav";

        h.Vm.PickWavCommand.Execute(null);

        Assert.Equal(@"C:\sounds\ding.wav", h.Vm.SelectedRule!.AudioFilePath);
        Assert.Equal(@"C:\sounds\ding.wav", h.Saved().Triggers[0].AudioFilePath);
    }

    [Fact]
    public void Test_overlays_sends_alerts_a_timer_and_a_synthetic_encounter()
    {
        using var h = new Harness();

        h.Vm.TestOverlaysCommand.Execute(null);

        IReadOnlyList<ActiveTimerSnapshot> timers = h.Pipeline.Triggers.Snapshot.Timers;
        Assert.Contains(timers, t => t.Kind == TimerKind.TextAlert);
        foreach (TimerOverlayCategory category in TimerOverlayCategoryExtensions.All)
        {
            Assert.Contains(timers, t => t.Kind == TimerKind.Countdown && t.Category == category); // one bar for every overlay
        }
        Assert.Equal("a test dummy", Assert.Single(h.Pipeline.LiveDps.Snapshot.Active).TargetName);
    }

    // ───────────────────────────── toolbar & settings ─────────────────────────────

    [Fact]
    public void Mute_lock_and_visibility_toggles_reach_the_host_and_are_saved()
    {
        using var h = new Harness();

        h.Vm.IsAudioMuted = true;
        h.Vm.AreOverlaysLocked = true;
        h.Vm.IsBuffsOverlayVisible = false;

        Assert.False(h.Audio.IsEnabled);
        Assert.Contains("lock:True", h.Overlays.Calls);
        Assert.Contains("visible:Buffs:False", h.Overlays.Calls);

        AppSettings saved = h.Saved();
        Assert.True(saved.IsAudioMuted);
        Assert.True(saved.BuffsOverlay.IsLocked);
        Assert.True(saved.DpsOverlay.IsLocked);
        Assert.False(saved.BuffsOverlay.IsVisible);
        Assert.True(saved.DpsOverlay.IsVisible);

        h.Vm.IsAudioMuted = false;
        Assert.True(h.Audio.IsEnabled);
    }

    [Fact]
    public void Moving_or_resizing_an_overlay_saves_its_new_bounds()
    {
        using var h = new Harness();

        h.Overlays.State[OverlayKind.Buffs] = h.Overlays.State[OverlayKind.Buffs] with { Left = 77, Top = 88, Width = 410, Height = 123 };
        h.Overlays.RaisePlacementChanged();

        OverlaySettings saved = h.Saved().BuffsOverlay;
        Assert.Equal(77, saved.Left);
        Assert.Equal(88, saved.Top);
        Assert.Equal(410, saved.Width);
        Assert.Equal(123, saved.Height);
    }

    [Fact]
    public void Encounter_timeout_is_clamped_applied_to_both_trackers_and_saved()
    {
        using var h = new Harness();

        h.Vm.EncounterTimeoutSeconds = 15;
        Assert.Equal(TimeSpan.FromSeconds(15), h.Pipeline.LiveDps.InactivityTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), h.Pipeline.HistoricalDps.InactivityTimeout);
        Assert.Equal(15, h.Saved().EncounterTimeoutSeconds);

        h.Vm.EncounterTimeoutSeconds = 500;
        Assert.Equal(AppSettings.MaxEncounterTimeoutSeconds, h.Vm.EncounterTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(AppSettings.MaxEncounterTimeoutSeconds), h.Pipeline.EncounterTimeout);

        h.Vm.EncounterTimeoutSeconds = 0;
        Assert.Equal(AppSettings.MinEncounterTimeoutSeconds, h.Vm.EncounterTimeoutSeconds);

        h.Vm.EncounterTimeoutSeconds = double.NaN;
        Assert.Equal(AppSettings.DefaultEncounterTimeoutSeconds, h.Vm.EncounterTimeoutSeconds);
    }

    // ───────────────────────────── DPS tab ─────────────────────────────

    [Fact]
    public void Live_encounters_and_the_participant_breakdown_follow_the_live_tracker()
    {
        using var h = new Harness();
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", Giant, 1000, 1));
        h.Pipeline.LiveDps.OnCombatEvent(Miss("Stackprime", Giant, 2));
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Oodles", Giant, 500, 3));

        h.Vm.Refresh();

        EncounterRowViewModel row = Assert.Single(h.Vm.Encounters);
        Assert.Same(row, h.Vm.SelectedEncounter); // the newest encounter is selected automatically
        Assert.Equal(Giant, row.TargetName);
        Assert.Equal(1500, row.TotalDamage);
        Assert.True(row.IsActive);
        Assert.Equal("00:02", row.DurationText);
        Assert.Contains(Giant, h.Vm.EncounterSummary);

        Assert.Equal(2, h.Vm.Participants.Count);
        ParticipantRowViewModel top = h.Vm.Participants[0];
        Assert.Equal(1, top.Rank);
        Assert.Equal("Stackprime", top.Name);
        Assert.Equal(1000, top.TotalDamage);
        Assert.Equal(100.0 * 1000 / 1500, top.DamagePercent, 3);
        Assert.Equal(1, top.Hits);
        Assert.Equal(1, top.Misses);
        Assert.Equal(50.0, top.HitRatePercent, 3);
        Assert.Equal(1000, top.MaxHit);
        Assert.Equal(500, top.ScaledDps, 3); // 1000 over the 2s encounter

        // New events update the same row objects in place.
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Oodles", Giant, 700, 3));
        h.Vm.Refresh();
        Assert.Same(row, Assert.Single(h.Vm.Encounters));
        Assert.Equal(2200, row.TotalDamage);
        Assert.Equal("Oodles", h.Vm.Participants[0].Name); // 1200 now outranks 1000
    }

    [Fact]
    public void A_selected_encounter_stays_selected_while_newer_ones_arrive()
    {
        using var h = new Harness();
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", Giant, 100, 1));
        h.Vm.Refresh();
        EncounterRowViewModel first = h.Vm.SelectedEncounter!;

        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", "a gnoll", 50, 3));
        h.Vm.Refresh();

        Assert.Equal(2, h.Vm.Encounters.Count);
        Assert.Same(first, h.Vm.SelectedEncounter);
        Assert.Equal("a gnoll", h.Vm.Encounters[0].TargetName); // newest first
        Assert.Equal(Giant, h.Vm.Participants.Count == 1 ? first.TargetName : string.Empty);
    }

    [Fact]
    public async Task Importing_a_log_adds_its_fights_to_the_one_list_without_disturbing_live_data()
    {
        using var h = new Harness();
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", "a gnoll", 50, 1));
        h.Dialogs.ImportFile = h.CreateLog("Stackprime", SampleLog);

        await h.Vm.ImportLogCommand.ExecuteAsync(null);

        Assert.False(h.Vm.IsImporting);
        Assert.Contains("Imported", h.Vm.ImportStatus);

        // Live and imported fights share one list.
        Assert.Equal(2, h.Vm.Encounters.Count);
        EncounterRowViewModel gnoll = h.Vm.Encounters.Single(r => r.TargetName == "a gnoll");
        EncounterRowViewModel giant = h.Vm.Encounters.Single(r => r.TargetName == Giant);
        Assert.False(gnoll.IsImported);
        Assert.True(giant.IsImported);
        Assert.Equal(802, giant.TotalDamage);
        Assert.False(giant.IsActive);
        Assert.NotEqual(gnoll.Key, giant.Key);

        h.Vm.SelectedEncounter = giant;
        Assert.Equal(new[] { "Oodles", "Stackprime" }, h.Vm.Participants.Select(p => p.Name));

        h.Vm.SelectedEncounter = gnoll;
        Assert.Equal("Stackprime", Assert.Single(h.Vm.Participants).Name);
    }

    [Fact]
    public async Task The_import_time_filter_limits_what_is_loaded()
    {
        using var h = new Harness();
        h.Dialogs.ImportFile = h.CreateLog("Stackprime", SampleLog);
        h.Vm.ImportStartText = "2026-10-21 11:45:02";
        h.Vm.ImportEndText = "2026-10-21 11:45:03";

        await h.Vm.ImportLogCommand.ExecuteAsync(null);

        Assert.Equal(660, Assert.Single(h.Vm.Encounters).TotalDamage); // 210 + 450
    }

    [Fact]
    public async Task Invalid_or_reversed_filters_are_rejected_before_any_file_is_picked()
    {
        using var h = new Harness();
        h.Dialogs.ImportFile = h.CreateLog("Stackprime", SampleLog);

        h.Vm.ImportStartText = "not a date";
        await h.Vm.ImportLogCommand.ExecuteAsync(null);
        Assert.Contains("start time", h.Vm.ImportStatus);

        h.Vm.ImportStartText = "2026-10-21 12:00:00";
        h.Vm.ImportEndText = "2026-10-21 11:00:00";
        await h.Vm.ImportLogCommand.ExecuteAsync(null);
        Assert.Contains("must not be later", h.Vm.ImportStatus);

        Assert.Equal(0, h.Dialogs.ImportPickCount);
        Assert.Empty(h.Pipeline.HistoricalDps.Snapshot.Completed);
    }

    [Fact]
    public async Task Cancelling_the_file_dialog_cancels_the_import_and_a_bad_file_reports_an_error()
    {
        using var h = new Harness();

        h.Dialogs.ImportFile = null;
        await h.Vm.ImportLogCommand.ExecuteAsync(null);
        Assert.False(h.Vm.IsImporting);

        h.Dialogs.ImportFile = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"), "eqlog_Nobody_Nowhere.txt");
        await h.Vm.ImportLogCommand.ExecuteAsync(null);
        Assert.StartsWith("Import failed", h.Vm.ImportStatus);
        Assert.False(h.Vm.IsImporting);
    }

    [Fact]
    public void The_cancel_button_is_only_enabled_during_an_import()
    {
        using var h = new Harness();

        Assert.False(h.Vm.CancelImportCommand.CanExecute(null));
        Assert.True(h.Vm.ImportLogCommand.CanExecute(null));
    }


    // ───────────────────────────── tab 3: buff & song tracker ─────────────────────────────

    [Fact]
    public async Task Browsing_for_the_spell_file_loads_the_duration_spells_and_saves_the_path()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = Harness.WriteSpellFile(h.SpellsPath);

        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);

        Assert.Equal(h.SpellsPath, h.Vm.SpellsFilePath);
        Assert.Contains("Loaded 3 duration spells", h.Vm.SpellsStatus);
        Assert.Contains("1 instant or permanent", h.Vm.SpellsStatus);
        SpellSuggestion wolf = Find(h, "Spirit of Wolf");
        Assert.Equal("ID 100 · 15:00", wolf.Detail);
        Assert.Equal(900, wolf.Spell.BaseDurationSeconds);
        Assert.Equal("You feel quick as a wolf.", wolf.Spell.CastOnYou);
        Assert.Empty(h.Vm.TrackedSpells); // loading the file tracks nothing

        AppSettings saved = h.Saved();
        Assert.Equal(h.SpellsPath, saved.SpellsFilePath);
        Assert.Equal(60, saved.DefaultCasterLevel);
    }

    [Fact]
    public async Task Cancelling_the_spell_file_dialog_changes_nothing()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = null;

        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, h.Vm.SpellsFilePath);
        Assert.Empty(h.Vm.TrackerSuggestions);
    }

    [Fact]
    public async Task Searching_offers_matches_by_name_or_id_and_ignores_one_letter_terms()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = Harness.WriteSpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);

        h.Vm.TrackerSearchText = "WOLF"; // case-insensitive
        Assert.Equal(100, Assert.Single(h.Vm.TrackerSuggestions).SpellId);

        h.Vm.TrackerSearchText = "sel";
        Assert.Equal(101, Assert.Single(h.Vm.TrackerSuggestions).SpellId);

        h.Vm.TrackerSearchText = "103"; // spell ids work too
        Assert.Equal("Short Buff", Assert.Single(h.Vm.TrackerSuggestions).Name);

        h.Vm.TrackerSearchText = "no such spell";
        Assert.Empty(h.Vm.TrackerSuggestions);

        h.Vm.TrackerSearchText = "s"; // too short to search
        Assert.Empty(h.Vm.TrackerSuggestions);
    }

    [Fact]
    public async Task Adding_a_spell_tracks_it_in_the_chosen_overlay_and_the_overlay_can_be_changed_later()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = Harness.WriteSpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);

        AddTracked(h, "Spirit of Wolf", TimerOverlayCategory.Songs);

        TrackedSpellRowViewModel wolf = Assert.Single(h.Vm.TrackedSpells);
        Assert.Equal(TimerOverlayCategory.Songs, wolf.Overlay);
        Assert.Equal("15:00", wolf.DurationText);
        Assert.Equal("1 spell tracked (1 on)", h.Vm.TrackedSummary);
        Assert.Equal(new[] { 100 }, h.Pipeline.Buffs.TrackedSpellIds);
        Assert.Equal(string.Empty, h.Vm.TrackerSearchText);

        AppSettings saved = h.Saved();
        TrackedSpellSetting savedWolf = Assert.Single(saved.TrackedSpells);
        Assert.Equal(100, savedWolf.SpellId);
        Assert.Equal(TimerOverlayCategory.Songs, savedWolf.Overlay);
        Assert.Equal(new[] { 100 }, saved.TrackedSpellIds);

        // The user's overlay wins over the spell file's own guess.
        h.Pipeline.Buffs.OnLine("Stackprime", "You begin singing Spirit of Wolf.", DateTime.UtcNow);
        ActiveTimerSnapshot timer = Assert.Single(h.Pipeline.Triggers.Snapshot.Timers);
        Assert.Equal("Stackprime: Spirit of Wolf", timer.Text);
        Assert.Equal(900, timer.DurationSeconds);
        Assert.Equal(TimerOverlayCategory.Songs, timer.Category);

        wolf.Overlay = TimerOverlayCategory.Debuffs;
        Assert.Equal(TimerOverlayCategory.Debuffs, Assert.Single(h.Saved().TrackedSpells).Overlay);
        h.Pipeline.Buffs.OnLine("Stackprime", "You feel quick as a wolf.", DateTime.UtcNow.AddSeconds(1));
        Assert.Contains(h.Pipeline.Triggers.Snapshot.Timers, t => t.Category == TimerOverlayCategory.Debuffs);
    }

    [Fact]
    public async Task Switching_a_spell_off_or_removing_it_stops_its_timers_and_is_saved()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = Harness.WriteSpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);
        AddTracked(h, "Spirit of Wolf", TimerOverlayCategory.Buffs);
        AddTracked(h, "Selo's Accelerando", TimerOverlayCategory.Songs);
        Assert.Equal(2, h.Vm.TrackedSpells.Count);

        TrackedSpellRowViewModel wolf = h.Vm.TrackedSpells.Single(r => r.SpellId == 100);
        wolf.IsEnabled = false;
        Assert.Equal(new[] { 101 }, h.Pipeline.Buffs.TrackedSpellIds);
        Assert.Equal("2 spells tracked (1 on)", h.Vm.TrackedSummary);
        Assert.Equal(new[] { 101 }, h.Saved().TrackedSpellIds);
        Assert.Equal(2, h.Saved().TrackedSpells.Count); // still on the list

        h.Vm.RemoveTrackedSpellCommand.Execute(wolf);
        Assert.Single(h.Vm.TrackedSpells);
        Assert.Equal(new[] { 101 }, h.Saved().TrackedSpells.Select(t => t.SpellId));
    }

    [Fact]
    public async Task Adding_a_spell_that_is_already_tracked_moves_it_to_the_new_overlay()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = Harness.WriteSpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);
        AddTracked(h, "Selo's Accelerando", TimerOverlayCategory.Buffs);

        AddTracked(h, "Selo's Accelerando", TimerOverlayCategory.Songs);

        TrackedSpellRowViewModel row = Assert.Single(h.Vm.TrackedSpells);
        Assert.Equal(TimerOverlayCategory.Songs, row.Overlay);
        Assert.Contains("already tracked", h.Vm.TrackerStatus);
    }

    [Fact]
    public async Task Adding_without_a_pick_explains_what_to_do()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = Harness.WriteSpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);

        h.Vm.TrackerSearchText = "zzzz";
        h.Vm.AddTrackedSpellCommand.Execute(null);

        Assert.Empty(h.Vm.TrackedSpells);
        Assert.Contains("Pick a spell", h.Vm.TrackerStatus);
    }

    [Fact]
    public async Task The_tracked_list_can_be_filtered_by_name_text_or_overlay()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = Harness.WriteSpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);
        AddTracked(h, "Spirit of Wolf", TimerOverlayCategory.Buffs);
        AddTracked(h, "Selo's Accelerando", TimerOverlayCategory.Songs);

        h.Vm.TrackedFilterText = "wolf";
        Assert.Equal(100, Assert.Single(h.Vm.VisibleTrackedSpells).SpellId);

        h.Vm.TrackedFilterText = "songs";
        Assert.Equal(101, Assert.Single(h.Vm.VisibleTrackedSpells).SpellId);

        h.Vm.TrackedFilterText = string.Empty;
        Assert.Equal(2, h.Vm.VisibleTrackedSpells.Count);
    }

    [Fact]
    public async Task Changing_the_caster_level_recalculates_durations_and_is_clamped_to_1_through_125()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = Harness.WriteSpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);
        Assert.Equal(180, Find(h, "Short Buff").Spell.BaseDurationSeconds); // ceil(60 / 2) ticks

        h.Vm.DefaultCasterLevel = 20;
        await h.Vm.SpellLoadTask;
        SpellSuggestion shortBuff = Find(h, "Short Buff");
        Assert.Equal(60, shortBuff.Spell.BaseDurationSeconds);
        Assert.Equal("ID 103 · 01:00", shortBuff.Detail);
        Assert.Contains("caster level 20", h.Vm.SpellsStatus);
        Assert.Equal(20, h.Saved().DefaultCasterLevel);

        h.Vm.DefaultCasterLevel = 0;
        await h.Vm.SpellLoadTask;
        Assert.Equal(1, h.Vm.DefaultCasterLevel);

        h.Vm.DefaultCasterLevel = 500;
        await h.Vm.SpellLoadTask;
        Assert.Equal(125, h.Vm.DefaultCasterLevel);
        Assert.Equal(63 * 6, Find(h, "Short Buff").Spell.BaseDurationSeconds);
    }

    [Fact]
    public async Task Tracked_spells_overlays_and_the_spell_file_are_restored_from_settings()
    {
        using var h = new Harness((s, dir) =>
        {
            s.SpellsFilePath = Harness.WriteSpellFile(Path.Combine(dir, "spells_us.txt"));
            s.DefaultCasterLevel = 20;
            s.TrackedSpells.Add(new TrackedSpellSetting { SpellId = 100, SpellName = "Spirit of Wolf", Overlay = TimerOverlayCategory.Songs });
            s.TrackedSpells.Add(new TrackedSpellSetting { SpellId = 9999, SpellName = "Gone Spell", Overlay = TimerOverlayCategory.Debuffs, IsEnabled = false });
        });

        // Active before the spell file finishes loading; the switched-off one is kept but not tracked.
        Assert.Equal(new[] { 100 }, h.Pipeline.Buffs.TrackedSpellIds);
        Assert.Equal("2 spells tracked (1 on)", h.Vm.TrackedSummary);

        await h.Vm.SpellLoadTask;

        Assert.Equal(20, h.Vm.DefaultCasterLevel);
        TrackedSpellRowViewModel wolf = h.Vm.TrackedSpells.Single(r => r.SpellId == 100);
        Assert.Equal("Spirit of Wolf", wolf.Name);
        Assert.Equal("15:00", wolf.DurationText);
        Assert.Equal(TimerOverlayCategory.Songs, wolf.Overlay);
        TrackedSpellRowViewModel gone = h.Vm.TrackedSpells.Single(r => r.SpellId == 9999);
        Assert.Equal("Gone Spell", gone.Name); // not in the file: still listed by its saved name
        Assert.Equal("?", gone.DurationText);
        Assert.False(File.Exists(h.SettingsPath)); // restoring is not a change

        h.Vm.IsAudioMuted = true; // any change saves everything
        AppSettings saved = h.Saved();
        Assert.Equal(new[] { 100, 9999 }, saved.TrackedSpells.Select(t => t.SpellId).OrderBy(i => i));
        Assert.Equal(TimerOverlayCategory.Debuffs, saved.TrackedSpells.Single(t => t.SpellId == 9999).Overlay);
        Assert.False(saved.TrackedSpells.Single(t => t.SpellId == 9999).IsEnabled);
    }

    [Fact]
    public async Task Spell_ids_from_older_settings_become_rows_and_get_the_spell_files_overlay_once()
    {
        using var h = new Harness((s, dir) =>
        {
            s.SpellsFilePath = WriteBossSpellFile(Path.Combine(dir, "spells_us.txt"));
            s.TrackedSpellIds.Add(300);
            s.TrackedSpellIds.Add(301);
            s.TrackedSpellIds.Add(302);
        });

        Assert.Equal(3, h.Vm.TrackedSpells.Count);
        await h.Vm.SpellLoadTask;

        Assert.Equal(TimerOverlayCategory.Buffs, h.Vm.TrackedSpells.Single(r => r.SpellId == 300).Overlay);
        Assert.Equal(TimerOverlayCategory.Debuffs, h.Vm.TrackedSpells.Single(r => r.SpellId == 301).Overlay);
        Assert.Equal(TimerOverlayCategory.Songs, h.Vm.TrackedSpells.Single(r => r.SpellId == 302).Overlay);

        h.Vm.TrackedSpells.Single(r => r.SpellId == 300).Overlay = TimerOverlayCategory.RaidTriggers; // the user's choice sticks
        Assert.Equal(TimerOverlayCategory.RaidTriggers, h.Saved().TrackedSpells.Single(t => t.SpellId == 300).Overlay);
    }

    [Fact]
    public async Task A_missing_spell_file_is_reported_without_breaking_the_app()
    {
        using var h = new Harness((s, dir) => s.SpellsFilePath = Path.Combine(dir, "gone", "spells_us.txt"));

        await h.Vm.SpellLoadTask;

        Assert.StartsWith("Could not load spells", h.Vm.SpellsStatus);
        Assert.Empty(h.Vm.TrackerSuggestions);
    }

    [Fact]
    public async Task The_buff_tracker_is_wired_into_live_monitoring_for_ticked_spells()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = Harness.WriteSpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);
        AddTracked(h, "Selo's Accelerando", TimerOverlayCategory.Songs);

        string log = h.CreateLog("Stackprime");
        h.Dialogs.LogFiles = new[] { log };
        h.Vm.AddLogCommand.Execute(null);
        await h.Vm.StartSessionAsync();

        await File.AppendAllTextAsync(log,
            "[Wed Oct 21 11:45:01 2026] You begin singing Selo's Accelerando.\n" +
            "[Wed Oct 21 11:45:02 2026] You feel your pace quicken.\n");

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (h.Pipeline.Triggers.Snapshot.Timers.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        ActiveTimerSnapshot timer = Assert.Single(h.Pipeline.Triggers.Snapshot.Timers);
        Assert.Equal("Stackprime: Selo's Accelerando", timer.Text);
        Assert.Equal(180, timer.DurationSeconds);
    }


    // ───────────────────────────── phase 6: five overlays ─────────────────────────────

    [Fact]
    public void Each_overlay_has_its_own_visibility_switch_that_reaches_the_host_and_is_saved()
    {
        using var h = new Harness();

        h.Vm.IsDebuffsOverlayVisible = false;
        h.Vm.IsSongsOverlayVisible = false;
        h.Vm.IsRaidTriggersOverlayVisible = false;
        h.Vm.IsDpsOverlayVisible = false;

        Assert.Contains("visible:Debuffs:False", h.Overlays.Calls);
        Assert.Contains("visible:Songs:False", h.Overlays.Calls);
        Assert.Contains("visible:RaidTriggers:False", h.Overlays.Calls);
        Assert.Contains("visible:Dps:False", h.Overlays.Calls);
        Assert.DoesNotContain("visible:Buffs:False", h.Overlays.Calls); // untouched

        AppSettings saved = h.Saved();
        Assert.True(saved.BuffsOverlay.IsVisible);
        Assert.False(saved.DebuffsOverlay.IsVisible);
        Assert.False(saved.SongsOverlay.IsVisible);
        Assert.False(saved.RaidTriggersOverlay.IsVisible);
        Assert.False(saved.DpsOverlay.IsVisible);
    }

    [Fact]
    public void Locking_applies_to_every_overlay_and_the_lock_state_is_restored_per_overlay()
    {
        using (var h = new Harness())
        {
            h.Vm.AreOverlaysLocked = true;
            AppSettings saved = h.Saved();
            Assert.All(new[] { saved.BuffsOverlay, saved.DebuffsOverlay, saved.SongsOverlay, saved.RaidTriggersOverlay, saved.DpsOverlay },
                o => Assert.True(o.IsLocked));
        }

        using var partiallyLocked = new Harness((s, _) => s.SongsOverlay = s.SongsOverlay with { IsLocked = true });
        Assert.False(partiallyLocked.Vm.AreOverlaysLocked); // only one of five is locked
        Assert.True(partiallyLocked.Overlays.State[OverlayKind.Songs].IsLocked);
    }

    [Fact]
    public void Moving_any_overlay_saves_that_overlays_bounds()
    {
        using var h = new Harness();

        h.Overlays.State[OverlayKind.Songs] = h.Overlays.State[OverlayKind.Songs] with { Left = 321, Top = 123 };
        h.Overlays.State[OverlayKind.Dps] = h.Overlays.State[OverlayKind.Dps] with { Width = 444 };
        h.Overlays.RaisePlacementChanged();

        AppSettings saved = h.Saved();
        Assert.Equal((321, 123), (saved.SongsOverlay.Left, saved.SongsOverlay.Top));
        Assert.Equal(444, saved.DpsOverlay.Width);
    }

    [Fact]
    public void A_triggers_overlay_choice_is_saved_and_routes_its_timers()
    {
        using var h = new Harness();
        h.Vm.AddRuleCommand.Execute(null);
        TriggerRuleViewModel rule = h.Vm.SelectedRule!;
        Assert.Equal(TimerOverlayCategory.RaidTriggers, rule.OverlayCategory); // the default
        rule.Pattern = "^go$";

        rule.OverlayCategory = TimerOverlayCategory.Songs;
        h.Pipeline.Triggers.OnLine("Stackprime", "go", DateTime.UtcNow);

        Assert.Equal(TimerOverlayCategory.Songs, Assert.Single(h.Pipeline.Triggers.Snapshot.Timers).Category);
        Assert.Equal(TimerOverlayCategory.Songs, Assert.Single(h.Saved().Triggers).OverlayCategory);
        Assert.Equal(new[] { "Buffs", "Debuffs", "Songs", "Raid Triggers", "CH Chain" }, h.Vm.CategoryOptions.Select(o => o.Display));
    }

    // ───────────────────────────── phase 6: critical hits ─────────────────────────────

    [Fact]
    public void The_breakdown_grid_shows_max_crit_and_crit_rate()
    {
        using var h = new Harness();
        h.Pipeline.LiveDps.OnCombatEvent(new CombatEvent(
            new DateTime(2026, 10, 21, 11, 45, 1), CombatEventKind.CriticalHit, "Stackprime", string.Empty, 900, DamageKind.Melee, null));
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", Giant, 900, 1));
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", Giant, 100, 2));
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", Giant, 100, 3));
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", Giant, 100, 4));

        h.Vm.Refresh();

        ParticipantRowViewModel row = Assert.Single(h.Vm.Participants);
        Assert.Equal(900, row.MaxCritHit);
        Assert.Equal(25.0, row.CritRatePercent, 3); // 1 crit in 4 hits
        Assert.Equal(900, row.MaxHit);
    }

    // ───────────────────────────── phase 6: spell categories & the raid boss builder ─────────────────────────────

    private static string WriteBossSpellFile(string path)
    {
        static string Row(int id, string name, string you, string other, string fades, int formula, int ticks, int goodEffect, int bardLevel)
        {
            string[] c = Enumerable.Repeat("0", 130).ToArray();
            c[0] = id.ToString();
            c[1] = name;
            c[6] = you;
            c[7] = other;
            c[8] = fades;
            c[13] = "1000";
            c[16] = formula.ToString();
            c[17] = ticks.ToString();
            c[83] = goodEffect.ToString();
            c[115] = bardLevel.ToString();
            return string.Join('^', c);
        }

        File.WriteAllText(path, string.Join("\n",
            Row(300, "Haste", "You feel quick.", " looks quick.", "Your quickness fades.", 11, 100, 1, 255),
            Row(301, "Disease Cloud", "You feel sick.", " looks sick.", "Your sickness fades.", 11, 100, 0, 255),
            Row(302, "Selo's Accelerando", "You pick up speed.", " moves fast.", "Your speed fades.", 11, 3, 1, 5),
            Row(303, "Dragon Roar", "You are blasted by a roar!", " is blasted by a roar.", "", 0, 0, 0, 255),
            Row(304, "Knockback", "", " is knocked back.", "", 0, 0, 0, 255),
            Row(305, "Silent Spell", "", "", "", 0, 0, 0, 255)) + "\n");
        return path;
    }

    [Fact]
    public async Task The_raid_boss_picker_finds_instant_spells_and_fills_in_the_match_text()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = WriteBossSpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);

        h.Vm.TrackerSearchText = "roar";
        Assert.Empty(h.Vm.TrackerSuggestions); // no duration: not a buff-tracker spell
        h.Vm.RaidSpellSearchText = "roar";
        SpellDefinition roar = Assert.Single(h.Vm.RaidSpellResults);
        Assert.Equal("Dragon Roar", roar.Name);

        h.Vm.RaidSpellSearchText = "knock";
        SpellDefinition knockback = Assert.Single(h.Vm.RaidSpellResults);
        h.Vm.SelectedRaidSpell = knockback;
        Assert.Equal("Knockback", h.Vm.RaidSpellName);
        Assert.Equal("is knocked back.", h.Vm.RaidMatchText); // only a cast-on-other message, trimmed

        h.Vm.RaidSpellSearchText = "silent";
        Assert.Empty(h.Vm.RaidSpellResults); // a spell without any landing message is not offered

        h.Vm.RaidSpellSearchText = "301"; // spell ids work too
        Assert.Equal("Disease Cloud", Assert.Single(h.Vm.RaidSpellResults).Name);
    }

    [Fact]
    public void The_raid_boss_picker_explains_when_no_spell_file_is_loaded()
    {
        using var h = new Harness();

        h.Vm.RaidSpellSearchText = "roar";

        Assert.Empty(h.Vm.RaidSpellResults);
        Assert.Contains("Load spells_us.txt", h.Vm.RaidSpellSearchStatus);
    }

    [Fact]
    public async Task Adding_a_raid_trigger_creates_the_timer_rule_saves_it_and_starts_timers_in_the_chosen_overlay()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = WriteBossSpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);

        h.Vm.RaidMobName = "Tormax";
        h.Vm.RaidSpellSearchText = "roar";
        h.Vm.SelectedRaidSpell = h.Vm.RaidSpellResults.Single();
        h.Vm.RaidRecastSeconds = 30;
        h.Vm.RaidCategory = TimerOverlayCategory.Debuffs;
        h.Vm.RaidStartTtsText = "Roar now";
        h.Vm.RaidWarningTtsText = "Roar in ten";
        h.Vm.AddRaidTriggerCommand.Execute(null);

        RaidTimerRuleViewModel row = Assert.Single(h.Vm.RaidRules);
        Assert.Equal("Tormax", row.MobText);
        Assert.Equal("Dragon Roar", row.SpellName);
        Assert.Equal("You are blasted by a roar!", row.MatchText);
        Assert.Equal("Debuffs", row.CategoryText);
        Assert.Contains("Added Tormax: Dragon Roar", h.Vm.RaidBuilderStatus);

        // The builder is ready for the boss's next ability: the spell part is cleared, the boss, recast and overlay are kept.
        Assert.Equal("Tormax", h.Vm.RaidMobName);
        Assert.Equal(string.Empty, h.Vm.RaidSpellName);
        Assert.Equal(string.Empty, h.Vm.RaidMatchText);
        Assert.Equal(TimerOverlayCategory.Debuffs, h.Vm.RaidCategory);

        // It is live straight away...
        h.Pipeline.RaidBosses.OnLine("Stackprime", "Tormax: You are blasted by a roar!", DateTime.UtcNow);
        ActiveTimerSnapshot timer = Assert.Single(h.Pipeline.Triggers.Snapshot.Timers);
        Assert.Equal("Tormax: Dragon Roar", timer.Text);
        Assert.Equal(TimerOverlayCategory.Debuffs, timer.Category);
        Assert.Equal(30, timer.DurationSeconds);

        // ...and saved.
        RaidBossTimerRule saved = Assert.Single(h.Saved().RaidBossTimers);
        Assert.Equal(303, saved.SpellId);
        Assert.Equal("Roar now", saved.StartTtsText);
        Assert.Equal("Roar in ten", saved.WarningTtsText);
        Assert.Equal(TimerOverlayCategory.Debuffs, saved.OverlayCategory);
    }

    [Fact]
    public void A_raid_trigger_can_be_typed_by_hand_but_incomplete_ones_are_rejected()
    {
        using var h = new Harness();

        h.Vm.AddRaidTriggerCommand.Execute(null);
        Assert.Contains("Pick a spell", h.Vm.RaidBuilderStatus);

        h.Vm.RaidSpellName = "Dragon Roar";
        h.Vm.AddRaidTriggerCommand.Execute(null);
        Assert.Contains("log text", h.Vm.RaidBuilderStatus);

        h.Vm.RaidMatchText = "roars";
        h.Vm.RaidRecastSeconds = 0;
        h.Vm.AddRaidTriggerCommand.Execute(null);
        Assert.Contains("at least 1 second", h.Vm.RaidBuilderStatus);
        Assert.Empty(h.Vm.RaidRules);

        h.Vm.RaidRecastSeconds = 48;
        h.Vm.AddRaidTriggerCommand.Execute(null);

        RaidTimerRuleViewModel row = Assert.Single(h.Vm.RaidRules);
        Assert.Equal("Any boss", row.MobText); // no boss typed
        Assert.Equal(48, row.RecastSeconds);
        Assert.Equal(0, h.Saved().RaidBossTimers[0].SpellId); // no spell was picked from the list
    }

    [Fact]
    public void Raid_rules_are_restored_toggled_and_removed()
    {
        var rule = new RaidBossTimerRule
        {
            MobName = "Velketor", SpellId = 303, SpellName = "Dragon Roar", MatchText = "blasted by a roar", RecastSeconds = 48,
        };
        using var h = new Harness((s, _) => s.RaidBossTimers.Add(rule));

        RaidTimerRuleViewModel row = Assert.Single(h.Vm.RaidRules);
        Assert.Equal("Velketor", row.MobText);
        Assert.Single(h.Pipeline.RaidBosses.Rules);
        Assert.False(File.Exists(h.SettingsPath)); // restoring is not a change

        row.IsEnabled = false;
        Assert.Empty(h.Pipeline.RaidBosses.Rules);
        Assert.False(Assert.Single(h.Saved().RaidBossTimers).IsEnabled);

        row.IsEnabled = true;
        Assert.Single(h.Pipeline.RaidBosses.Rules);

        h.Vm.RemoveRaidTriggerCommand.Execute(row);
        Assert.Empty(h.Vm.RaidRules);
        Assert.Empty(h.Pipeline.RaidBosses.Rules);
        Assert.Empty(h.Saved().RaidBossTimers);
    }

    // ───────────────────────────── phase 6: theme ─────────────────────────────

    [Fact]
    public void The_saved_theme_is_applied_at_startup_without_counting_as_a_change()
    {
        using var h = new Harness((s, _) => s.Theme.PresetName = "VeliousFrost");

        Assert.Equal(ThemePresets.VeliousFrost, h.Theme.Last);
        Assert.Equal("VeliousFrost", h.Vm.ThemePresetName);
        Assert.True(h.Vm.IsVeliousFrostSelected);
        Assert.False(h.Vm.IsObsidianSlateSelected);
        Assert.False(File.Exists(h.SettingsPath));

        Assert.Equal(9, h.Vm.ThemeColors.Count);
        Assert.Equal("#0EA5E9", h.Vm.ThemeColors.Single(c => c.Key == ThemeColorKey.Accent).Hex);
    }

    [Fact]
    public void Choosing_a_preset_applies_it_immediately_updates_the_color_boxes_and_is_saved()
    {
        using var h = new Harness();
        Assert.Equal(ThemePresets.ObsidianSlate, h.Theme.Last);
        int applied = h.Theme.Applied.Count;

        h.Vm.SelectThemePresetCommand.Execute("KunarkEmber");

        Assert.Equal(applied + 1, h.Theme.Applied.Count);
        Assert.Equal(ThemePresets.KunarkEmber, h.Theme.Last);
        Assert.True(h.Vm.IsKunarkEmberSelected);
        Assert.Equal("#EA580C", h.Vm.ThemeColors.Single(c => c.Key == ThemeColorKey.Accent).HexText);
        Assert.Equal("KunarkEmber", h.Saved().Theme.PresetName);

        h.Vm.SelectThemePresetCommand.Execute("not a preset name"); // unknown names fall back to Obsidian Slate
        Assert.Equal("ObsidianSlate", h.Vm.ThemePresetName);
    }

    [Fact]
    public void Typing_a_valid_hex_color_starts_a_custom_theme_from_the_current_preset_and_applies_it_live()
    {
        using var h = new Harness();
        h.Vm.SelectThemePresetCommand.Execute("KunarkEmber");
        ThemeColorEditorViewModel accent = h.Vm.ThemeColors.Single(c => c.Key == ThemeColorKey.Accent);

        accent.HexText = "#ff00ff";

        Assert.Equal("Custom", h.Vm.ThemePresetName);
        Assert.True(h.Vm.IsCustomThemeSelected);
        Assert.Equal("#FF00FF", accent.Hex);
        ThemePalette applied = h.Theme.Last;
        Assert.Equal("#FF00FF", applied.Accent);
        Assert.Equal(ThemePresets.KunarkEmber.WindowBg, applied.WindowBg); // everything else started from Kunark Ember

        ThemeConfig saved = h.Saved().Theme;
        Assert.Equal("Custom", saved.PresetName);
        Assert.Equal("#FF00FF", saved.AccentHex);
        Assert.Equal(ThemePresets.KunarkEmber.CardBg, saved.CardBgHex);
    }

    [Fact]
    public void Custom_colors_are_remembered_while_presets_are_used()
    {
        using var h = new Harness();
        h.Vm.ThemeColors.Single(c => c.Key == ThemeColorKey.Accent).HexText = "#123ABC";

        h.Vm.SelectThemePresetCommand.Execute("VeliousFrost");
        Assert.Equal(ThemePresets.VeliousFrost, h.Theme.Last);
        Assert.Equal("#123ABC", h.Saved().Theme.AccentHex); // still stored

        h.Vm.SelectThemePresetCommand.Execute("Custom");
        Assert.Equal("#123ABC", h.Theme.Last.Accent);
        Assert.Equal("#123ABC", h.Vm.ThemeColors.Single(c => c.Key == ThemeColorKey.Accent).HexText);
    }

    [Fact]
    public void An_incomplete_or_invalid_hex_color_is_not_applied()
    {
        using var h = new Harness();
        ThemeColorEditorViewModel accent = h.Vm.ThemeColors.Single(c => c.Key == ThemeColorKey.Accent);
        int applied = h.Theme.Applied.Count;

        accent.HexText = "#12";
        Assert.False(accent.IsValid);
        accent.HexText = "blue";
        Assert.False(accent.IsValid);

        Assert.Equal(applied, h.Theme.Applied.Count);
        Assert.Equal("ObsidianSlate", h.Vm.ThemePresetName);
        Assert.Equal(ThemePresets.ObsidianSlate.Accent, accent.Hex); // the swatch keeps the last good color

        accent.HexText = "#112233";
        Assert.True(accent.IsValid);
        Assert.Equal("#112233", h.Theme.Last.Accent);
    }

    [Fact]
    public void A_saved_custom_theme_is_restored_exactly()
    {
        using var h = new Harness((s, _) =>
        {
            s.Theme.PresetName = "Custom";
            s.Theme.SetCustomPalette(ThemePresets.ObsidianSlate.With(ThemeColorKey.BuffsBar, "#00FF00"));
        });

        Assert.Equal("#00FF00", h.Theme.Last.BuffsBar);
        Assert.True(h.Vm.IsCustomThemeSelected);
        Assert.Equal("#00FF00", h.Vm.ThemeColors.Single(c => c.Key == ThemeColorKey.BuffsBar).Hex);
    }

    [Fact]
    public void The_theme_bar_toggles()
    {
        using var h = new Harness();
        Assert.False(h.Vm.IsThemeBarOpen);

        h.Vm.ToggleThemeBarCommand.Execute(null);
        Assert.True(h.Vm.IsThemeBarOpen);

        h.Vm.ToggleThemeBarCommand.Execute(null);
        Assert.False(h.Vm.IsThemeBarOpen);
    }

    // ───────────────────────────── shutdown ─────────────────────────────

    [Fact]
    public async Task Shutdown_saves_stops_monitoring_closes_overlays_and_disposes_audio_once()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Stackprime") };
        h.Vm.AddLogCommand.Execute(null);
        await h.Vm.StartSessionAsync();
        h.Overlays.State[OverlayKind.Dps] = h.Overlays.State[OverlayKind.Dps] with { Left = 555 };
        Assert.True(h.Pipeline.IsLiveRunning);

        h.Vm.Shutdown();

        Assert.False(h.Pipeline.IsLiveRunning);
        Assert.Equal(1, h.Overlays.CloseAllCalls);
        Assert.Equal(1, h.Audio.DisposeCount);

        AppSettings saved = new AppSettingsService(h.SettingsPath).Load();
        Assert.Equal("Stackprime", Assert.Single(saved.WatchedLogs).CharacterName);
        Assert.Equal(555, saved.DpsOverlay.Left); // overlay state captured before the windows were closed

        h.Vm.Shutdown(); // idempotent
        Assert.Equal(1, h.Overlays.CloseAllCalls);
        Assert.Equal(1, h.Audio.DisposeCount);
    }

    [Fact]
    public void Nothing_is_saved_after_shutdown()
    {
        using var h = new Harness();
        h.Vm.Shutdown();
        string before = File.ReadAllText(h.SettingsPath);

        h.Vm.EncounterTimeoutSeconds = 42;

        Assert.Equal(before, File.ReadAllText(h.SettingsPath));
    }
}
