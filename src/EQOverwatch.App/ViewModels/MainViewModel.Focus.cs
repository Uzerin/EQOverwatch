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

    private static string DurationKey(string character, string server) => $"{character}|{server}";

    /// <summary>What was chosen for the character, or none.</summary>
    public DurationProfile GetDurationProfile(string character, string server)
    {
        lock (_durationGate)
        {
            return _durationProfiles.GetValueOrDefault(DurationKey(character, server)) ?? DurationProfile.None;
        }
    }

    public void SetDurationProfile(string character, string server, DurationProfile profile)
    {
        lock (_durationGate)
        {
            if (profile.IsEmpty)
            {
                _durationProfiles.Remove(DurationKey(character, server));
            }
            else
            {
                _durationProfiles[DurationKey(character, server)] = profile;
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
                if (!string.IsNullOrWhiteSpace(s.CharacterName) && !profile.IsEmpty)
                {
                    _durationProfiles[DurationKey(s.CharacterName, s.ServerName ?? string.Empty)] = profile;
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
                    int bar = kv.Key.IndexOf('|');
                    return new DurationProfileSetting
                    {
                        CharacterName = kv.Key[..bar],
                        ServerName = kv.Key[(bar + 1)..],
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

        DurationProfile profile = GetDurationProfile(log.CharacterName, log.ServerName);
        if (profile.IsEmpty)
        {
            return null;
        }

        int classIndex = EqClass.TryNormalize(_pipeline.Roster.GetClass(name, log.ServerName), out string className)
            ? EqClass.Names.ToList().IndexOf(className)
            : -1;
        return new CasterDuration(classIndex, profile);
    }

    private FocusEffectsViewModel CreateFocusViewModel(string name, string server) =>
        new(_pipeline.Spells.Database.DurationFoci, GetDurationProfile(name, server), profile => SetDurationProfile(name, server, profile));

    [RelayCommand]
    private void OpenFocusEffects(WatchedLogConfig? log)
    {
        if (log is null)
        {
            return;
        }

        string name = log.CharacterName, server = log.ServerName;
        FocusEffectsViewModel focus = CreateFocusViewModel(name, server);
        var viewModel = new CharacterDataViewModel(
            _characterData, () => EqFolderPath, name, server, () => _pipeline.Roster.GetClass(name, server),
            CharacterDataViewModel.FocusTab, focus);
        _characterDataPresenter?.Show(viewModel);
    }
}
