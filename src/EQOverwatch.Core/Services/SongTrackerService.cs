using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.Core.Services;

/// <summary>
/// Puts every bard song on the Songs overlay without the song having to be on the Spells list. The song is looked up by name in the
/// spell file and its bar lasts the song's own duration.
/// <list type="bullet">
/// <item><description>Your characters: "You begin singing X." starts (or restarts) "Character: X" at once, at that character's level when
/// it is known.</description></item>
/// <item><description>Other bards, only while <see cref="ShowAllBards"/> is on: "Trick begins singing X." or "Trick begins to sing a song.
/// &lt;X&gt;" starts "Trick: X" at the spell file's caster level. When several of your logs see the same song, one bar is kept.</description></item>
/// </list>
/// Bars are filed under the character whose log saw the song, so they show while that character is logged in.
/// </summary>
public sealed class SongTrackerService : ITriggerLineSink
{
    private const string YouBeginSinging = "You begin singing ";
    private const string OtherSingingMarker = " begins singing ";
    private const string OtherSingMarker = " begins to sing a song. <";

    private readonly SpellDatabaseService _spells;
    private readonly TriggerEngineService _timers;
    private readonly object _gate = new();

    // (bard, song) → the character the bar is filed under, so several of your logs share one bar per other bard's song.
    private readonly Dictionary<(string Bard, string Song), string> _otherBars = new();

    private Func<string, bool>? _isMine;
    private Func<string, DateTime, bool>? _isActive;
    private Func<string, int>? _levelOf;
    private long _barsStarted;

    public SongTrackerService(SpellDatabaseService spells, TriggerEngineService timers)
    {
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        _timers = timers ?? throw new ArgumentNullException(nameof(timers));
    }

    /// <summary>Show other players' songs too (off: only songs your characters sing).</summary>
    public bool ShowAllBards { get; set; }

    /// <summary>Raised when a song bar starts: whom the song is from, the song, its duration in seconds and when.</summary>
    public event Action<string, string, int, DateTime>? SongStarted;

    /// <summary>How many song bars have been started.</summary>
    public long BarsStarted => Interlocked.Read(ref _barsStarted);

    /// <summary>Tells the tracker which names are your own characters (their songs come from their own logs).</summary>
    public void SetOwnCharacterProbe(Func<string, bool>? isMine) => Volatile.Write(ref _isMine, isMine);

    /// <summary>Tells the tracker which characters are logged in, so another bard's bar stays with a character that can show it.</summary>
    public void SetActivityProbe(Func<string, DateTime, bool>? isActive) => Volatile.Write(ref _isActive, isActive);

    /// <summary>Gives a character's level (0 when not known), so your own songs last as long as they do at that level.</summary>
    public void SetLevelProvider(Func<string, int>? levelOf) => Volatile.Write(ref _levelOf, levelOf);

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (message.Length < 12 || message[^1] is not ('.' or '>'))
        {
            return;
        }

        if (message.StartsWith(YouBeginSinging, StringComparison.Ordinal))
        {
            if (message[^1] == '.')
            {
                string song = message[YouBeginSinging.Length..^1].ToString();
                StartOwn(characterName, song, receivedUtc);
            }

            return;
        }

        if (!ShowAllBards || !TrySplitOther(message, out string bard, out string otherSong))
        {
            return;
        }

        if (Volatile.Read(ref _isMine)?.Invoke(bard) == true || bard.Equals(characterName, StringComparison.OrdinalIgnoreCase))
        {
            return; // your own bard's log reports the song itself
        }

        StartOther(characterName, bard, otherSong, receivedUtc);
    }

    /// <summary>The seconds a song lasts at <paramref name="level"/> (the spell file's caster level when 0); 0 when it has no duration.</summary>
    public static int SongSeconds(SpellInfo song, int level)
    {
        if (level <= 0)
        {
            return song.BaseDurationSeconds;
        }

        int ticks = SpellDatabaseService.ComputeBaseTicks(song.DurationFormula, level, song.MaxDurationTicks);
        return ticks <= 0 ? song.BaseDurationSeconds : ticks * 6;
    }

    private void StartOwn(string character, string songName, DateTime now)
    {
        if (_spells.Database.FindByName(songName) is not { } song)
        {
            return;
        }

        int level = Volatile.Read(ref _levelOf)?.Invoke(character) ?? 0;
        Start(character, character, song, SongSeconds(song, level), now);
    }

    private void StartOther(string observer, string bard, string songName, DateTime now)
    {
        if (_spells.Database.FindByName(songName) is not { } song)
        {
            return;
        }

        string subject;
        lock (_gate)
        {
            var key = (bard.ToLowerInvariant(), song.Name.ToLowerInvariant());
            Func<string, DateTime, bool>? isActive = Volatile.Read(ref _isActive);
            if (_otherBars.TryGetValue(key, out string? filed)
                && (filed.Equals(observer, StringComparison.OrdinalIgnoreCase) || isActive?.Invoke(filed, now) != false))
            {
                subject = filed; // keep one bar for everyone who saw it
            }
            else
            {
                if (filed is not null)
                {
                    _timers.CancelTimer(BuffTrackerService.TimerSourceId, filed, Label(bard, song.Name));
                }

                subject = observer;
                _otherBars[key] = observer;
                if (_otherBars.Count > 500)
                {
                    _otherBars.Clear();
                    _otherBars[key] = observer;
                }
            }
        }

        Start(subject, bard, song, song.BaseDurationSeconds, now);
    }

    private void Start(string subject, string singer, SpellInfo song, int seconds, DateTime now)
    {
        if (seconds <= 0)
        {
            return;
        }

        _timers.StartTimer(
            BuffTrackerService.TimerSourceId, BuffTrackerService.TimerSourceName, subject, Label(singer, song.Name), seconds, now,
            TimerOverlayCategory.Songs);
        Interlocked.Increment(ref _barsStarted);
        SongStarted?.Invoke(singer, song.Name, seconds, now);
    }

    private static string Label(string singer, string song) => $"{singer}: {song}";

    /// <summary>"Trick begins singing X." or "Trick begins to sing a song. &lt;X&gt;".</summary>
    internal static bool TrySplitOther(ReadOnlySpan<char> message, out string bard, out string song)
    {
        bard = string.Empty;
        song = string.Empty;

        int index = message.IndexOf(OtherSingingMarker, StringComparison.Ordinal);
        int marker = OtherSingingMarker.Length;
        if (index > 0 && message[^1] == '.')
        {
            song = message[(index + marker)..^1].ToString();
        }
        else
        {
            index = message.IndexOf(OtherSingMarker, StringComparison.Ordinal);
            marker = OtherSingMarker.Length;
            if (index <= 0 || message[^1] != '>')
            {
                return false;
            }

            song = message[(index + marker)..^1].ToString();
        }

        ReadOnlySpan<char> name = message[..index];
        foreach (char c in name)
        {
            if (!char.IsLetter(c))
            {
                return false; // "A Shissar bard begins singing" is a mob, not a player
            }
        }

        bard = name.ToString();
        return song.Length > 0;
    }
}
