using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Services;

/// <summary>
/// Reads incoming tells aloud. A log line such as <c>Catiua tells you, 'I SEE YOUUUUUU'</c> in the log of Stackprimeiiv is spoken as
/// "Catiua tells Stackprimeiiv: I SEE YOUUUUUU", through the same speech path (voice, volume, rate, mute) as trigger audio. It sees the
/// lines of every trigger-enabled log, so a tell to any of your watched characters is spoken with that character's name. Lines read
/// while a log is imported never reach it, so old tells are not spoken.
/// </summary>
public sealed class TellReaderService : ITriggerLineSink
{
    /// <summary>The longest message spoken; a longer one is cut off.</summary>
    public const int MaxSpokenLength = 200;

    private static readonly Guid SourceId = new("5f0c9b7e-3d52-4c1a-9a4e-7b2d6e1f8a30");
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(2);

    private readonly TriggerEngineService _engine;
    private readonly object _gate = new();
    private string _lastKey = string.Empty;
    private DateTime _lastUtc;
    private volatile bool _isEnabled = true;

    public TellReaderService(TriggerEngineService engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>Turns reading tells aloud on or off (on by default).</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        // Almost every line is not a tell, so test the span before allocating anything.
        if (!_isEnabled || string.IsNullOrWhiteSpace(characterName) || !TellLineParser.TryParse(message, out string sender, out string text))
        {
            return;
        }

        if (text.Length > MaxSpokenLength)
        {
            text = text[..MaxSpokenLength];
        }

        string key = characterName + "|" + sender + "|" + text;
        lock (_gate)
        {
            if (key.Equals(_lastKey, StringComparison.Ordinal) && receivedUtc - _lastUtc < RepeatWindow)
            {
                return; // the same tell twice in a row
            }

            _lastKey = key;
            _lastUtc = receivedUtc;
        }

        _engine.PublishAudio(SourceId, "Tell", characterName, $"{sender} tells {characterName}: {text}", receivedUtc);
    }
}

/// <summary>Reads <c>Name tells you, 'message'</c>: a tell sent to the character whose log it is.</summary>
public static class TellLineParser
{
    private const string Marker = " tells you, '";
    private const int MaxNameLength = 24;

    public static bool TryParse(ReadOnlySpan<char> message, out string sender, out string text)
    {
        sender = string.Empty;
        text = string.Empty;

        int at = message.IndexOf(Marker.AsSpan(), StringComparison.Ordinal);
        if (at < 2 || at > MaxNameLength || message[^1] != '\'' || message.Length <= at + Marker.Length)
        {
            return false;
        }

        // A player's name is one word of letters; this keeps NPC speech, "You tell ..." and chat channels out.
        ReadOnlySpan<char> name = message[..at];
        foreach (char c in name)
        {
            if (!char.IsLetter(c))
            {
                return false;
            }
        }

        string spoken = message[(at + Marker.Length)..^1].ToString().Trim();
        if (spoken.Length == 0)
        {
            return false;
        }

        sender = name.ToString();
        text = spoken;
        return true;
    }
}
