using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Parsing;

namespace EQOverwatch.Core.Models;

/// <summary>
/// A log file under watch. Every log feeds the DPS parse; <see cref="IsTriggerEnabled"/> may be set on any number of logs.
/// The tail service reads the flag per line, so toggling takes effect immediately without a restart.
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

    private string _guildText = string.Empty;

    /// <summary>The character's guild from /who (empty when not seen), for the Characters list.</summary>
    public string GuildText
    {
        get => _guildText;
        set => SetProperty(ref _guildText, value ?? string.Empty);
    }

    public string DisplayName => string.IsNullOrEmpty(ServerName) ? CharacterName : $"{CharacterName} ({ServerName})";

    /// <summary>The character's level as last seen in a /who line or level-up message, or 0 (shown as a dash).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelText))]
    private int _level;

    /// <summary>The character's class as last seen in a /who line ("Shadow Knight"), or empty (shown as a dash).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClassText), nameof(IsTank), nameof(CanChainHeal))]
    private string _className = string.Empty;

    private bool? _tankChoice;

    /// <summary>
    /// A tank: no aggro alert when a mob hits it. Warriors, paladins and shadow knights are tanks until changed; a choice made on the
    /// Characters tab is kept (see <see cref="TankChoice"/>).
    /// </summary>
    public bool IsTank
    {
        get => _tankChoice ?? IsTankClass(ClassName);
        set
        {
            if (_tankChoice != value)
            {
                _tankChoice = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The Tank box as chosen on the Characters tab, or null while it follows the class.</summary>
    public bool? TankChoice
    {
        get => _tankChoice;
        set
        {
            _tankChoice = value;
            OnPropertyChanged(nameof(IsTank));
        }
    }

    /// <summary>Warrior, Paladin and Shadow Knight (and their level titles' base classes as /who shows them).</summary>
    public static bool IsTankClass(string? className) =>
        className is not null
        && (className.Contains("Warrior", StringComparison.OrdinalIgnoreCase)
            || className.Contains("Paladin", StringComparison.OrdinalIgnoreCase)
            || className.Contains("Shadow Knight", StringComparison.OrdinalIgnoreCase));

    /// <summary>When the newest /outputfile export for this character was read ("Oct 28 18:12"), or "No data yet".</summary>
    [ObservableProperty]
    private string _dataText = "No data yet";

    /// <summary>The character's total time played from the last /played ("13d 22h 59m"), or a dash.</summary>
    [ObservableProperty]
    private string _playedText = "—";

    /// <summary>When the character was created ("May 27, 2026"), from /played, or a dash.</summary>
    [ObservableProperty]
    private string _createdText = "—";

    /// <summary>The account the character is on ("Ream box", or the names of characters sharing its bank), or a dash.</summary>
    [ObservableProperty]
    private string _accountText = "—";

    /// <summary>The account's color (#RRGGBB), or empty for the normal text color.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccountColor))]
    private string _accountColor = string.Empty;

    public bool HasAccountColor => AccountColor.Length > 0;

    /// <summary>True while the character's log had a line in the last minute (it is logged in).</summary>
    [ObservableProperty]
    private bool _isActiveNow;

    /// <summary>How long ago anyone on the character's account last played ("Now", "2d ago").</summary>
    [ObservableProperty]
    private string _accountPlayedText = "—";

    public string LevelText => Level > 0 ? Level.ToString(CultureInfo.InvariantCulture) : "—";

    public string ClassText => ClassName.Length > 0 ? ClassName : "—";

    /// <summary>True for a cleric, or while the class is not known yet: only they get a CH Chain Position.</summary>
    public bool CanChainHeal => ClassName.Length == 0 || ClassName.Equals("Cleric", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool _isTriggerEnabled = true;

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
