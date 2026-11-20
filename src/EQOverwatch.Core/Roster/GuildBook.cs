using System.Text.Json;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Roster;

/// <summary>
/// Players' guilds from /who lines ("[60 Warlord (Warrior)] Stackprime (Ogre) &lt;Leadership Optional&gt; ZONE: ..."), per server, kept in a
/// JSON file. A /who line without a guild leaves the guild as it was (the player may be anonymous to it). Thread-safe.
/// </summary>
public sealed partial class GuildBook : ITriggerLineSink
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _guilds = new(StringComparer.OrdinalIgnoreCase);
    private string? _path;
    private bool _dirty;

    [GeneratedRegex(@"^\[[^\]]+\] (?<name>[A-Za-z]+)(?:\s+\([^)]*\))?\s+<(?<guild>[^>]{1,64})>", RegexOptions.CultureInvariant)]
    private static partial Regex WhoWithGuild();

    public void SetStoragePath(string path)
    {
        lock (_gate)
        {
            _path = path;
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } saved)
                {
                    foreach ((string key, string guild) in saved)
                    {
                        _guilds[key] = guild;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // a damaged file starts empty
            }
        }
    }

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc) =>
        OnLine(characterName, string.Empty, message, receivedUtc);

    public void OnLine(string characterName, string serverName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (message.Length < 16 || message[0] != '[' || message.IndexOf('<') < 0)
        {
            return;
        }

        Match who = WhoWithGuild().Match(message.ToString());
        if (who.Success)
        {
            Learn(who.Groups["name"].Value, serverName, who.Groups["guild"].Value.Trim());
        }
    }

    /// <summary>Notes a player's guild on a server.</summary>
    public void Learn(string name, string? server, string guild)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(guild))
        {
            return;
        }

        lock (_gate)
        {
            string key = Key(name, server);
            if (!_guilds.TryGetValue(key, out string? known) || known != guild)
            {
                _guilds[key] = guild;
                _dirty = true;
            }
        }
    }

    /// <summary>The player's guild on the server, or empty when no /who has shown one.</summary>
    public string Get(string name, string? server)
    {
        lock (_gate)
        {
            return _guilds.GetValueOrDefault(Key(name, server)) ?? string.Empty;
        }
    }

    /// <summary>Writes the file when something changed (call now and then, and on exit).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty || _path is null)
            {
                return;
            }

            try
            {
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_guilds));
                File.Move(temp, _path, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // tried again next time
            }
        }
    }

    private static string Key(string name, string? server) => name.Trim() + "|" + (server ?? string.Empty).Trim().ToLowerInvariant();
}
