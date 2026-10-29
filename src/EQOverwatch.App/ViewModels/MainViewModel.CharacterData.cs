using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Discovery;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

/// <summary>The Characters tab's Inventory / Spellbook / Missing spells buttons and the data window they open.</summary>
public sealed partial class MainViewModel
{
    private readonly CharacterDataStore _characterData;
    private readonly Services.ICharacterDataPresenter? _characterDataPresenter;

    /// <summary>The last explanation shown when a character has no <c>/outputfile</c> data yet.</summary>
    [ObservableProperty]
    private string _characterDataStatus = string.Empty;

    [RelayCommand]
    private void OpenInventory(WatchedLogConfig? log) => OpenCharacterData(log, OutputFileKind.Inventory);

    [RelayCommand]
    private void OpenSpellbook(WatchedLogConfig? log) => OpenCharacterData(log, OutputFileKind.Spellbook);

    [RelayCommand]
    private void OpenMissingSpells(WatchedLogConfig? log) => OpenCharacterData(log, OutputFileKind.MissingSpells);

    private void OpenCharacterData(WatchedLogConfig? log, OutputFileKind kind)
    {
        if (log is null)
        {
            return;
        }

        // Always look at the game folder first: the files may have been written since the last time. Every character is read, because
        // the Missing spells tab also lists the scrolls the other characters carry.
        _characterData.ScanAll(EqFolderPath);
        CharacterData? data = _characterData.Find(log.CharacterName, log.ServerName);
        RefreshCharacterInfo();

        if (data is null || !data.Has(kind))
        {
            string message = NoCharacterDataMessage(log.CharacterName, kind, EqDirectoryScanner.Normalize(EqFolderPath));
            CharacterDataStatus = message;
            _characterDataPresenter?.ShowMessage($"No data for {log.CharacterName}", message);
            return;
        }

        CharacterDataStatus = string.Empty;
        int tab = kind switch
        {
            OutputFileKind.Inventory => CharacterDataViewModel.InventoryTab,
            OutputFileKind.Spellbook => CharacterDataViewModel.SpellbookTab,
            _ => CharacterDataViewModel.MissingSpellsTab,
        };

        string name = log.CharacterName;
        var viewModel = new CharacterDataViewModel(
            _characterData, () => EqFolderPath, name, log.ServerName, () => _pipeline.Roster.GetClass(name), tab);
        _characterDataPresenter?.Show(viewModel);
    }

    /// <summary>What to tell someone who clicks a data button before EverQuest has written the file.</summary>
    public static string NoCharacterDataMessage(string character, OutputFileKind kind, string eqFolder)
    {
        if (string.IsNullOrWhiteSpace(eqFolder))
        {
            return "Set your EverQuest folder first (Settings tab). EverQuest saves the /outputfile files there, and EQ Overwatch reads them from it.";
        }

        (string what, string command) = kind switch
        {
            OutputFileKind.Inventory => ("inventory", "/outputfile inventory"),
            OutputFileKind.Spellbook => ("spellbook", "/outputfile spellbook"),
            _ => ("missing spells", "/outputfile missingspells"),
        };

        return $"No {what} data for {character} yet.\n\nIn EverQuest, log in as {character} and type:\n\n    {command}\n\n" +
               $"Then click the button again. EverQuest saves the file in {eqFolder}.";
    }

    /// <summary>"Updated Oct 28 18:12", or "No data yet": the Characters grid's "Data from" column.</summary>
    private string DescribeCharacterData(WatchedLogConfig log)
    {
        CharacterData? data = _characterData.Find(log.CharacterName, log.ServerName);
        return data?.NewestUtc is { } newest ? CharacterDataViewModel.Describe(newest) : "No data yet";
    }
}
