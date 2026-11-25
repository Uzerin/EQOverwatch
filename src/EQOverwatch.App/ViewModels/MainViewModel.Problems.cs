using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EQOverwatch.App.ViewModels;

// What went wrong lately (Settings: a log that couldn't be read, a save that failed, an error), and a bar after the app ran into an
// error last time, with a button that copies the details from crash.log for pasting in Discord. Nothing is sent anywhere.
public sealed partial class MainViewModel
{
    private const int MaxProblems = 20;
    private const int MaxCrashTextLength = 6000;
    private DateTime? _lastCrashSeenUtc;
    private string _crashDetails = string.Empty;

    /// <summary>Problems since the app opened, newest first ("14:02  Cannot read eqlog_Ream_vaniki.txt: ...").</summary>
    public ObservableCollection<string> Problems { get; } = new();

    public bool HasProblems => Problems.Count > 0;

    /// <summary>"EQ Overwatch ran into an error last time." while there are new crash.log entries; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrashNotice))]
    private string _crashNotice = string.Empty;

    public bool HasCrashNotice => CrashNotice.Length > 0;

    /// <summary>Adds a problem to the list (from any thread).</summary>
    public void AddProblem(string text)
    {
        void Add()
        {
            Problems.Insert(0, $"{_clock():HH:mm}  {text}");
            while (Problems.Count > MaxProblems)
            {
                Problems.RemoveAt(Problems.Count - 1);
            }

            OnPropertyChanged(nameof(HasProblems));
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Add);
        }
        else
        {
            Add();
        }
    }

    [RelayCommand]
    private void CopyProblems()
    {
        if (Problems.Count > 0)
        {
            _clipboard.SetText($"{Core.AppInfo.Title}{Environment.NewLine}{string.Join(Environment.NewLine, Problems)}");
            StatusMessage = "Copied the problems. Paste them in Discord if you'd like help.";
        }
    }

    [RelayCommand]
    private void ClearProblems()
    {
        Problems.Clear();
        OnPropertyChanged(nameof(HasProblems));
    }

    /// <summary>Looks in crash.log for entries newer than the last one shown.</summary>
    private void CheckCrashLog(DateTime? seenUtc)
    {
        _lastCrashSeenUtc = seenUtc;
        if (string.IsNullOrEmpty(_dataDirectory))
        {
            return;
        }

        string path = Path.Combine(_dataDirectory, Core.Updates.CrashLog.FileName);
        List<(DateTime Utc, string Text)> entries;
        try
        {
            entries = File.Exists(path) ? CrashEntries(File.ReadAllText(path)) : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        List<(DateTime Utc, string Text)> fresh = entries.Where(e => seenUtc is null || e.Utc > seenUtc).ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        if (seenUtc is null && fresh.Count == entries.Count && fresh[^1].Utc < DateTime.UtcNow.AddDays(-7))
        {
            _lastCrashSeenUtc = fresh[^1].Utc; // old errors from before this was added: not worth a bar
            return;
        }

        string text = string.Join(Environment.NewLine, fresh.Select(e => e.Text));
        _crashDetails = text.Length > MaxCrashTextLength ? text[^MaxCrashTextLength..] : text;
        _lastCrashSeenUtc = fresh[^1].Utc;
        CrashNotice = fresh.Count == 1
            ? "EQ Overwatch ran into an error last time."
            : $"EQ Overwatch ran into {fresh.Count} errors since you last looked.";
        AddProblem(CrashNotice);
    }

    /// <summary>The entries of a crash.log: each starts with "===== 2026-11-24 18:02:11 UTC".</summary>
    public static List<(DateTime Utc, string Text)> CrashEntries(string log)
    {
        var entries = new List<(DateTime, string)>();
        foreach (Match m in Regex.Matches(log, @"^===== (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) UTC.*?(?=^===== |\z)", RegexOptions.Multiline | RegexOptions.Singleline))
        {
            if (DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime utc))
            {
                entries.Add((utc, m.Value.Trim()));
            }
        }

        return entries;
    }

    [RelayCommand]
    private void CopyCrashDetails()
    {
        _clipboard.SetText($"{Core.AppInfo.Title}{Environment.NewLine}{_crashDetails}");
        StatusMessage = "Copied the error details. Paste them in the EQ Overwatch Discord.";
    }

    [RelayCommand]
    private void DismissCrashNotice()
    {
        CrashNotice = string.Empty;
        RequestSave();
    }
}
