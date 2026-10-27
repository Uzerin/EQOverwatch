using EQCompanion.Core.Pipeline;

namespace EQCompanion.Core.Sharing;

/// <summary>A shared trigger spotted in a log line.</summary>
/// <param name="Item">The validated rule. Nothing about it is enabled yet: the user decides.</param>
/// <param name="Sender">Who said it ("You" is replaced by the character whose log saw it).</param>
/// <param name="Payload">The Base64 payload (used to recognize the same offer again).</param>
public sealed record SharedTriggerOffer(SharedItem Item, string Sender, string Payload, DateTime ReceivedUtc);

/// <summary>
/// Watches the trigger-enabled logs for <c>{EQO:...}</c>, <c>{EQC:...}</c> and <c>{EQNag:...}</c> tokens in any chat channel and reports
/// each valid one through <see cref="Offered"/>. The same token seen again within <see cref="DuplicateWindow"/> (several watched characters
/// share a raid channel) is reported once. Offers are only ever <i>reported</i>: accepting is up to the user.
/// </summary>
public sealed class SharedTriggerScanner : ITriggerLineSink
{
    /// <summary>The same payload this soon after the last one is the same message heard by another log.</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(10);

    private const int MaxRemembered = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _recent = new(StringComparer.Ordinal);

    /// <summary>Raised on the pipeline's consumer thread. Handlers must not block.</summary>
    public event Action<SharedTriggerOffer>? Offered;

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        // Almost no line contains "{EQ": decide that without allocating.
        if (message.Length < 8 || message.IndexOf("{EQ", StringComparison.Ordinal) < 0)
        {
            return;
        }

        string line = message.ToString();
        foreach ((string tag, string payload) in TriggerShareSerializer.FindTokens(line))
        {
            if (IsDuplicate(payload, receivedUtc))
            {
                continue;
            }

            if (!TriggerShareSerializer.TryDecode(tag, payload, out SharedItem? item, out _) || item is null)
            {
                continue; // damaged, truncated or not ours: ignore silently
            }

            Offered?.Invoke(new SharedTriggerOffer(item, ExtractSender(line, characterName), payload, receivedUtc));
        }
    }

    private bool IsDuplicate(string payload, DateTime now)
    {
        lock (_gate)
        {
            if (_recent.TryGetValue(payload, out DateTime last) && now >= last && now - last < DuplicateWindow)
            {
                return true;
            }

            if (_recent.Count >= MaxRemembered)
            {
                foreach (string stale in _recent.Where(p => now - p.Value >= DuplicateWindow || p.Value > now).Select(p => p.Key).ToList())
                {
                    _recent.Remove(stale);
                }

                if (_recent.Count >= MaxRemembered)
                {
                    _recent.Clear();
                }
            }

            _recent[payload] = now;
            return false;
        }
    }

    /// <summary>"Ream tells the raid, '...'" gives "Ream"; "You tell your raid, ..." gives the observing character.</summary>
    private static string ExtractSender(string line, string observedBy)
    {
        int space = line.IndexOf(' ');
        if (space <= 0)
        {
            return observedBy;
        }

        string first = line[..space];
        return first.Equals("You", StringComparison.Ordinal) ? observedBy : first;
    }
}
