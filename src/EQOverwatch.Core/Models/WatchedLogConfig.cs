using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Parsing;

namespace EQOverwatch.Core.Models;

/// <summary>
/// A log file under watch. <see cref="IsTriggerEnabled"/> may be set on any number of logs;
/// <see cref="IsPrimaryDps"/> is mutually exclusive across a <see cref="WatchedLogCollection"/>.
/// The tail service reads both flags per line, so toggling takes effect immediately without a restart.
/// </summary>
public sealed partial class WatchedLogConfig : ObservableObject
{
    public WatchedLogConfig(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        FilePath = Path.GetFullPath(filePath);

        if (LogFileName.TryParse(FilePath, out string character, out string server))
        {
            CharacterName = character;
            ServerName = server;
        }
        else
        {
            CharacterName = Path.GetFileNameWithoutExtension(FilePath);
            ServerName = string.Empty;
        }
    }

    public string FilePath { get; }

    /// <summary>Extracted from <c>eqlog_&lt;Character&gt;_&lt;Server&gt;.txt</c>.</summary>
    public string CharacterName { get; }

    public string ServerName { get; }

    public string DisplayName => string.IsNullOrEmpty(ServerName) ? CharacterName : $"{CharacterName} ({ServerName})";

    /// <summary>The character's level as last seen in a /who line or level-up message, or 0 (shown as a dash).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelText))]
    private int _level;

    /// <summary>The character's class as last seen in a /who line ("Shadow Knight"), or empty (shown as a dash).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClassText))]
    private string _className = string.Empty;

    /// <summary>When the newest /outputfile export for this character was read ("Oct 28 18:12"), or "No data yet".</summary>
    [ObservableProperty]
    private string _dataText = "No data yet";

    public string LevelText => Level > 0 ? Level.ToString(CultureInfo.InvariantCulture) : "—";

    public string ClassText => ClassName.Length > 0 ? ClassName : "—";

    [ObservableProperty]
    private bool _isTriggerEnabled = true;

    [ObservableProperty]
    private bool _isPrimaryDps;

    /// <summary>Highest Complete Heal chain position a character can hold.</summary>
    public const int MaxChChainPosition = 12;

    private int _chChainPosition;

    /// <summary>
    /// This character's slot in the Complete Heal chain (1-12), or 0 for none. At most one character holds each slot
    /// (see <see cref="WatchedLogCollection"/>).
    /// </summary>
    public int ChChainPosition
    {
        get => _chChainPosition;
        set => SetProperty(ref _chChainPosition, Math.Clamp(value, 0, MaxChChainPosition));
    }
}
