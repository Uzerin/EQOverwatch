using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Roster;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.App.ViewModels;

// Each character's spell-duration focus and AA choices (the Focus Effects tab). They lengthen the bars of the buffs, debuffs and
// DoTs that character casts.
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, DurationProfile> _durationProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _durationGate = new();

    private static string DurationKey(string character, string server, string persona = "") => $"{character}|{server}|{persona}";

    /// <summary>What was chosen for the character's persona (or, failing that, for the character before personas), or none.</summary>
    public DurationProfile GetDurationProfile(string character, string server, string persona = "")
    {
        lock (_durationGate)
        {
            return _durationProfiles.GetValueOrDefault(DurationKey(character, server, persona))
                   ?? _durationProfiles.GetValueOrDefault(DurationKey(character, server))
                   ?? DurationProfile.None;
        }
    }

    public void SetDurationProfile(string character, string server, DurationProfile profile, string persona = "")
    {
        lock (_durationGate)
        {
            // Saved under the persona; an empty choice still overrides an older one made before personas.
            _durationProfiles[DurationKey(character, server, persona)] = profile;
            if (profile.IsEmpty && persona.Length == 0)
            {
                _durationProfiles.Remove(DurationKey(character, server, persona));
            }
        }

        if (!_restoring)
        {
            RequestSave();
        }
    }

    private void RestoreDurationProfiles(IEnumerable<DurationProfileSetting>? saved)
    {
        lock (_durationGate)
        {
            _durationProfiles.Clear();
            foreach (DurationProfileSetting s in saved ?? Enumerable.Empty<DurationProfileSetting>())
            {
                var profile = new DurationProfile(s.BeneficialFocusId, s.DetrimentalFocusId, Math.Clamp(s.AaPercent, 0, 100));
                if (!string.IsNullOrWhiteSpace(s.CharacterName) && (!profile.IsEmpty || !string.IsNullOrEmpty(s.Persona)))
                {
                    _durationProfiles[DurationKey(s.CharacterName, s.ServerName ?? string.Empty, s.Persona ?? string.Empty)] = profile;
                }
            }
        }
    }

    private List<DurationProfileSetting> DurationProfileSettings()
    {
        lock (_durationGate)
        {
            return _durationProfiles
                .Select(kv =>
                {
                    string[] parts = kv.Key.Split('|');
                    return new DurationProfileSetting
                    {
                        CharacterName = parts[0],
                        ServerName = parts.Length > 1 ? parts[1] : string.Empty,
                        Persona = parts.Length > 2 ? parts[2] : string.Empty,
                        BeneficialFocusId = kv.Value.BeneficialFocusId,
                        DetrimentalFocusId = kv.Value.DetrimentalFocusId,
                        AaPercent = kv.Value.AaPercent,
                    };
                })
                .OrderBy(s => s.CharacterName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>The buff tracker's view of a character: its class (for the spell level) and its duration choices.</summary>
    private CasterDuration? CasterDurationFor(string name)
    {
        WatchedLogConfig? log = Logs.FirstOrDefault(l => l.CharacterName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (log is null)
        {
            return null;
        }

        // The class follows the persona that is on, and so do the focus choices.
        bool known = EqClass.TryNormalize(_pipeline.Roster.GetClass(name, log.ServerName), out string className);
        DurationProfile profile = GetDurationProfile(log.CharacterName, log.ServerName, known ? className : string.Empty);

        // Learned alternate abilities lengthen spells too. An AA percent set by hand in Focus Effects replaces the percent ones.
        IReadOnlyList<AaDurationBonus> aa = AaDurations.For(_pipeline.Progress.AaRanks(log.CharacterName, log.ServerName, known ? className : null));
        if (profile.AaPercent > 0)
        {
            aa = aa.Where(b => b.Percent == 0).ToList();
        }

        profile = profile with { AaBonuses = aa };
        if (profile.IsEmpty)
        {
            return null;
        }

        int classIndex = known ? EqClass.Names.ToList().IndexOf(className) : -1;
        return new CasterDuration(classIndex, profile);
    }

    private FocusEffectsViewModel CreateFocusViewModel(string name, string server, string persona = "") =>
        new(_pipeline.Spells.Database.DurationFoci, GetDurationProfile(name, server, persona), profile => SetDurationProfile(name, server, profile, persona));

    [RelayCommand]
    private void OpenFocusEffects(WatchedLogConfig? log)
    {
        if (log is null)
        {
            return;
        }

        _characterDataPresenter?.Show(CreateCharacterWindow(log, CharacterDataViewModel.FocusTab));
    }
}
