using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQOverwatch.Core.Progress;

/// <summary>One learned alternate ability: its rank, the points spent on it, when it was learned and where the row comes from.</summary>
/// <param name="Source">"Log", "Added by you" or "Edited by you".</param>
public sealed record AaProgress(string Name, int Rank, int Points, DateTime? LearnedUtc, string Source)
{
    public const string FromLog = "Log";
    public const string Added = "Added by you";
    public const string Edited = "Edited by you";
}

// Alternate abilities per persona: "You have gained the ability "Origin" at a cost of 0 ability points." (rank 1) and
// "You have improved Healing Adept 3 at a cost of 6 ability points." (rank 3). Entries made by hand stay until the log shows that
// ability learned again after the edit; then the log's rank takes over.
public sealed partial class ProgressStore
{
    private const string GainedAbilityPrefix = "You have gained the ability \"";
    private const string ImprovedPrefix = "You have improved ";

    [GeneratedRegex(@"^You have gained the ability ""(?<name>.+)"" at a cost of (?<cost>\d+) ability points?\.$", RegexOptions.CultureInvariant)]
    private static partial Regex GainedAbilityRegex();

    [GeneratedRegex(@"^You have improved (?<name>.+) (?<rank>\d+) at a cost of (?<cost>\d+) ability points?\.$", RegexOptions.CultureInvariant)]
    private static partial Regex ImprovedRegex();

    /// <summary>Edits by hand, per character, server and persona ("Ream|frostreaver|Cleric"), then per ability name.</summary>
    private readonly Dictionary<string, Dictionary<string, AaEdit>> _aaEdits = new(StringComparer.OrdinalIgnoreCase);

    private static bool WantedAa(string message) =>
        message.StartsWith(GainedAbilityPrefix, StringComparison.Ordinal)
        || (message.StartsWith(ImprovedPrefix, StringComparison.Ordinal) && message.EndsWith(" point.", StringComparison.Ordinal) | message.EndsWith(" points.", StringComparison.Ordinal));

    /// <summary>Reads an ability line into the persona that is on. True when it was one.</summary>
    private static bool ReadAa(PersonaState persona, string text, long time)
    {
        Match gained = text.StartsWith(GainedAbilityPrefix, StringComparison.Ordinal) ? GainedAbilityRegex().Match(text) : Match.Empty;
        Match improved = gained.Success || !text.StartsWith(ImprovedPrefix, StringComparison.Ordinal) ? Match.Empty : ImprovedRegex().Match(text);
        if (!gained.Success && !improved.Success)
        {
            return false;
        }

        Match m = gained.Success ? gained : improved;
        string name = m.Groups["name"].Value.Trim();
        int rank = improved.Success && int.TryParse(improved.Groups["rank"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int r) ? r : 1;
        int cost = int.TryParse(m.Groups["cost"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int c) ? c : 0;
        if (name.Length == 0)
        {
            return false;
        }

        persona.Aas ??= new Dictionary<string, AaState>(StringComparer.OrdinalIgnoreCase);
        if (!persona.Aas.TryGetValue(name, out AaState? state))
        {
            persona.Aas[name] = state = new AaState();
        }

        state.Rank = Math.Max(state.Rank, rank);
        state.Points += cost;
        state.Time = Math.Max(state.Time, time);
        return true;
    }

    /// <summary>"Your Meditative Blanket feels alive with power." gives the item and the message ending, or null for any other line.</summary>
    public static (string Item, string Ending)? ItemFocusLine(string message)
    {
        if (!message.StartsWith("Your ", StringComparison.Ordinal) || message.Length > 120)
        {
            return null;
        }

        foreach (string ending in Spells.FocusKinds.ItemMessageEndings)
        {
            if (message.EndsWith(" " + ending, StringComparison.Ordinal))
            {
                string item = message[5..^(ending.Length + 1)].Trim();
                return item.Length is > 0 and < 80 ? (item, ending) : null;
            }
        }

        return null;
    }

    /// <summary>The items whose focus fired in a character's logs, with the message and how often, most often first.</summary>
    public IReadOnlyList<(string Item, string Ending, int Count)> FocusFires(string character, string server)
    {
        lock (_gate)
        {
            if (!_characters.TryGetValue(Key(character, server), out CharacterState? state) || state.FocusFires is null)
            {
                return Array.Empty<(string, string, int)>();
            }

            return state.FocusFires
                .Select(p => (Item: p.Key[..p.Key.IndexOf('|')], Ending: p.Key[(p.Key.IndexOf('|') + 1)..], Count: p.Value))
                .OrderByDescending(f => f.Count)
                .ToList();
        }
    }

    private string EditKey(string character, string server, string persona) => Key(character, server) + "|" + persona;

    private string PersonaOrCurrent(string character, string server, string? persona) =>
        persona ?? (_characters.TryGetValue(Key(character, server), out CharacterState? state) ? state.Current : string.Empty);

    /// <summary>A persona's abilities (the current one when <paramref name="persona"/> is null), A to Z, with your own edits applied.</summary>
    public IReadOnlyList<AaProgress> Aas(string character, string server, string? persona = null)
    {
        lock (_gate)
        {
            string who = PersonaOrCurrent(character, server, persona);
            Dictionary<string, AaState> logged = FindPersona(character, server, who)?.Aas ?? new Dictionary<string, AaState>();
            Dictionary<string, AaEdit> edits = _aaEdits.GetValueOrDefault(EditKey(character, server, who)) ?? new Dictionary<string, AaEdit>();
            var rows = new List<AaProgress>();
            foreach (string name in logged.Keys.Concat(edits.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                AaState? log = logged.GetValueOrDefault(name);
                AaEdit? edit = edits.GetValueOrDefault(name);
                if (edit is not null && (log is null || log.Time <= edit.Time))
                {
                    if (!edit.Hidden)
                    {
                        rows.Add(new AaProgress(edit.Name, edit.Rank, log?.Points ?? 0, log is null ? null : LogHistoryReader.FromUnix(log.Time),
                            log is null ? AaProgress.Added : AaProgress.Edited));
                    }
                }
                else if (log is not null)
                {
                    rows.Add(new AaProgress(name, log.Rank, log.Points, LogHistoryReader.FromUnix(log.Time), AaProgress.FromLog));
                }
            }

            return rows.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Each ability's rank for a persona (the current one when null), your edits included.</summary>
    public IReadOnlyDictionary<string, int> AaRanks(string character, string server, string? persona = null) =>
        Aas(character, server, persona).ToDictionary(a => a.Name, a => a.Rank, StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds or changes an ability by hand; the log takes over again once it shows the ability learned after this.</summary>
    public void SetAa(string character, string server, string? persona, string name, int rank, DateTime nowUtc)
    {
        name = name.Trim();
        if (name.Length == 0 || rank < 1)
        {
            return;
        }

        StoreEdit(character, server, persona, new AaEdit { Name = name, Rank = Math.Min(rank, 999), Time = LogHistoryReader.ToUnix(nowUtc) });
    }

    /// <summary>Hides an ability until the log shows it learned again.</summary>
    public void RemoveAa(string character, string server, string? persona, string name, DateTime nowUtc) =>
        StoreEdit(character, server, persona, new AaEdit { Name = name.Trim(), Hidden = true, Time = LogHistoryReader.ToUnix(nowUtc) });

    private void StoreEdit(string character, string server, string? persona, AaEdit edit)
    {
        lock (_gate)
        {
            string key = EditKey(character, server, PersonaOrCurrent(character, server, persona));
            if (!_aaEdits.TryGetValue(key, out Dictionary<string, AaEdit>? edits))
            {
                _aaEdits[key] = edits = new Dictionary<string, AaEdit>(StringComparer.OrdinalIgnoreCase);
            }

            edits[edit.Name] = edit;
            Changed();
        }
    }

    private void LoadAaEdits(JsonElement? saved)
    {
        if (saved is not { ValueKind: JsonValueKind.Object } edits)
        {
            return;
        }

        try
        {
            foreach ((string key, Dictionary<string, AaEdit> byName) in edits.Deserialize<Dictionary<string, Dictionary<string, AaEdit>>>() ?? new())
            {
                _aaEdits[key] = new Dictionary<string, AaEdit>(byName, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
        }
    }

    public sealed class AaState
    {
        public int Rank { get; set; }

        public int Points { get; set; }

        public long Time { get; set; }
    }

    public sealed class AaEdit
    {
        public string Name { get; set; } = string.Empty;

        public int Rank { get; set; }

        public bool Hidden { get; set; }

        public long Time { get; set; }
    }
}
