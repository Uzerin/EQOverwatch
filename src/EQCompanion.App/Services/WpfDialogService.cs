using System.Windows;
using EQCompanion.App.Services;
using Microsoft.Win32;

namespace EQCompanion.App.Services;

/// <summary>Real file dialogs, owned by the main window.</summary>
public sealed class WpfDialogService : IDialogService
{
    private const string LogFilter = "EverQuest logs (eqlog_*_*.txt)|eqlog_*_*.txt|All files (*.*)|*.*";

    public string[]? PickLogFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select EverQuest log files",
            Filter = LogFilter,
            Multiselect = true,
            CheckFileExists = true,
        };

        return dialog.ShowDialog(Owner()) == true ? dialog.FileNames : null;
    }

    public string? PickImportLogFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a log file to import",
            Filter = LogFilter,
            CheckFileExists = true,
        };

        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    public string? PickWavFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a sound file",
            Filter = "Wave audio (*.wav)|*.wav",
            CheckFileExists = true,
        };

        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    public string? PickSpellsFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select spells_us.txt (in your EverQuest folder)",
            Filter = "EverQuest spell data (spells_us.txt)|spells_us.txt|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    public string? PickEqFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select your EverQuest folder (the one that contains Logs and spells_us.txt)",
            Multiselect = false,
        };

        return dialog.ShowDialog(Owner()) == true ? dialog.FolderName : null;
    }

    private static Window? Owner() => Application.Current?.MainWindow;
}
