using System.Text.Json;
using System.Text.Json.Serialization;
using EQOverwatch.Core.Discovery;

namespace EQOverwatch.Core.Characters;

/// <summary>Everything the <c>/outputfile</c> files have told the app about one character. Each part has its own capture time.</summary>
public sealed class CharacterData
{
    public string Name { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;

    /// <summary>"CLR", from the spellbook and missing-spells file names. Empty until one of them has been read.</summary>
    public string ClassCode { get; set; } = string.Empty;

    /// <summary>The name of the other PC this character's files came from, or empty when they were read on this PC.</summary>
    public string FromPc { get; set; } = string.Empty;

    public DateTime? InventoryUtc { get; set; }
    public List<InventoryItem> Inventory { get; set; } = new();
    public List<KeyRingEntry> KeyRing { get; set; } = new();

    public DateTime? SpellbookUtc { get; set; }
    public List<SpellListEntry> Spellbook { get; set; } = new();

    public DateTime? MissingUtc { get; set; }
    public List<SpellListEntry> Missing { get; set; } = new();

    /// <summary>The newest capture time of any part, or null when nothing has been read.</summary>
    [JsonIgnore]
    public DateTime? NewestUtc => new[] { InventoryUtc, SpellbookUtc, MissingUtc }.Max();

    public bool Has(OutputFileKind kind) => kind switch
    {
        OutputFileKind.Inventory => InventoryUtc is not null,
        OutputFileKind.Spellbook => SpellbookUtc is not null,
        _ => MissingUtc is not null,
    };
}

/// <summary>The shared bank of one server. Every character's inventory file lists it, so it is kept once, from the newest file.</summary>
public sealed class SharedBankData
{
    public string Server { get; set; } = string.Empty;
    public DateTime? CapturedUtc { get; set; }
    public string FromCharacter { get; set; } = string.Empty;
    public List<InventoryItem> Items { get; set; } = new();
}

/// <summary>
/// Reads the <c>/outputfile</c> files in the game folder and keeps the newest data per character: one JSON file per character
/// (<c>Name_server.json</c>) and one per server's shared bank (<c>shared_server.json</c>) in <c>directory</c>. With no directory
/// everything stays in memory. A file is only read again when it is newer than what was stored, so scanning is cheap.
/// </summary>
public sealed class CharacterDataStore
{
    private const string SharedPrefix = "shared_";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string? _directory;
    private readonly object _gate = new();
    private readonly Dictionary<string, CharacterData> _characters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SharedBankData> _shared = new(StringComparer.OrdinalIgnoreCase);

    public CharacterDataStore(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
        Load();
    }

    /// <summary>Raised after a file was read and changed what is stored. May run on any thread.</summary>
    public event Action? Changed;

    /// <summary>The character's data (the one from the newest file when the name exists on several servers), or null.</summary>
    public CharacterData? Find(string name, string? server = null)
    {
        lock (_gate)
        {
            IEnumerable<CharacterData> matches = _characters.Values.Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(server))
            {
                matches = matches.Where(c => c.Server.Equals(server, StringComparison.OrdinalIgnoreCase));
            }

            return matches.OrderByDescending(c => c.NewestUtc ?? DateTime.MinValue).FirstOrDefault();
        }
    }

    /// <summary>Every character whose files have been read, as a snapshot.</summary>
    public IReadOnlyList<CharacterData> All()
    {
        lock (_gate)
        {
            return _characters.Values.ToList();
        }
    }

    /// <summary>The shared bank of every server that has been read, as a snapshot.</summary>
    public IReadOnlyList<SharedBankData> SharedBanks()
    {
        lock (_gate)
        {
            return _shared.Values.ToList();
        }
    }

    public SharedBankData? FindShared(string server)
    {
        lock (_gate)
        {
            return _shared.GetValueOrDefault(server ?? string.Empty);
        }
    }

    /// <summary>Reads the character's output files from the game folder. Returns how many files changed what is stored.</summary>
    public int Scan(string? eqFolder, string name, string? server = null)
    {
        string folder = EqDirectoryScanner.Normalize(eqFolder);
        if (folder.Length == 0 || string.IsNullOrWhiteSpace(name) || !Directory.Exists(folder))
        {
            return 0;
        }

        int imported = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, name + "_*"))
            {
                if (!CharacterOutputParser.TryParseFileName(file, out OutputFileInfo info)
                    || !info.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(server) && !info.Server.Equals(server, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (Import(file))
                {
                    imported++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed is the same as one without files.
        }

        return imported;
    }

    /// <summary>Reads every output file in the game folder (at start-up). Returns how many files changed what is stored.</summary>
    public int ScanAll(string? eqFolder)
    {
        string folder = EqDirectoryScanner.Normalize(eqFolder);
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            return 0;
        }

        int imported = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*_*-*"))
            {
                if (CharacterOutputParser.TryParseFileName(file, out _) && Import(file))
                {
                    imported++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return imported;
    }

    /// <summary>
    /// Drops what is saved from output files that are no longer in the game folder: per kind (inventory, spellbook, missing spells),
    /// and a character left with nothing is forgotten. Characters whose files came from another PC are left alone, and nothing is
    /// removed when the folder is not set, cannot be listed, or does not look like a game folder. Returns how many parts were dropped.
    /// </summary>
    public int PruneMissingFiles(string? eqFolder)
    {
        string folder = EqDirectoryScanner.Normalize(eqFolder);
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            return 0;
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*_*-*"))
            {
                if (CharacterOutputParser.TryParseFileName(file, out OutputFileInfo info))
                {
                    present.Add(PresenceKey(info.Name, info.Server, info.Kind));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0; // a folder that cannot be listed says nothing about its files
        }

        if (present.Count == 0 && !LooksLikeGameFolder(folder))
        {
            return 0; // probably the wrong folder rather than one with every file deleted
        }

        int dropped = 0;
        var toSave = new List<CharacterData>();
        var toDelete = new List<string>();
        var sharedToDelete = new List<SharedBankData>();
        lock (_gate)
        {
            foreach (CharacterData data in _characters.Values.Where(c => c.FromPc.Length == 0).ToList())
            {
                bool changed = false;
                if (data.InventoryUtc is not null && !present.Contains(PresenceKey(data.Name, data.Server, OutputFileKind.Inventory)))
                {
                    data.Inventory = new List<InventoryItem>();
                    data.KeyRing = new List<KeyRingEntry>();
                    data.InventoryUtc = null;
                    changed = true;
                    dropped++;
                }

                if (data.SpellbookUtc is not null && !present.Contains(PresenceKey(data.Name, data.Server, OutputFileKind.Spellbook)))
                {
                    data.Spellbook = new List<SpellListEntry>();
                    data.SpellbookUtc = null;
                    changed = true;
                    dropped++;
                }

                if (data.MissingUtc is not null && !present.Contains(PresenceKey(data.Name, data.Server, OutputFileKind.MissingSpells)))
                {
                    data.Missing = new List<SpellListEntry>();
                    data.MissingUtc = null;
                    changed = true;
                    dropped++;
                }

                if (!changed)
                {
                    continue;
                }

                if (data.NewestUtc is null)
                {
                    _characters.Remove(Key(data.Name, data.Server));
                    toDelete.Add($"{Key(data.Name, data.Server)}.json");
                }
                else
                {
                    toSave.Add(data);
                }
            }

            // A server's shared bank comes from inventory files: when none is left for the server, the bank goes too.
            foreach (SharedBankData bank in _shared.Values.ToList())
            {
                bool stillHeld = _characters.Values.Any(c => c.InventoryUtc is not null && c.Server.Equals(bank.Server, StringComparison.OrdinalIgnoreCase));
                if (!stillHeld)
                {
                    _shared.Remove(bank.Server);
                    sharedToDelete.Add(bank);
                    dropped++;
                }
            }
        }

        foreach (CharacterData data in toSave)
        {
            Save(data);
        }

        foreach (string file in toDelete)
        {
            Remove(file);
        }

        foreach (SharedBankData bank in sharedToDelete)
        {
            Remove($"{SharedPrefix}{bank.Server}.json");
        }

        if (dropped > 0)
        {
            Changed?.Invoke();
        }

        return dropped;
    }

    /// <summary>Forgets everything saved for one character. It comes back if its files are read again. Returns false when none was saved.</summary>
    public bool Forget(string name, string server)
    {
        bool removed;
        lock (_gate)
        {
            removed = _characters.Remove(Key(name, server));
        }

        if (!removed)
        {
            return false;
        }

        Remove($"{Key(name, server)}.json");
        Changed?.Invoke();
        return true;
    }

    private static string PresenceKey(string name, string server, OutputFileKind kind) => $"{name}|{server}|{kind}";

    private static bool LooksLikeGameFolder(string folder) =>
        Directory.Exists(EqDirectoryScanner.GetLogsFolder(folder))
        || EqDirectoryScanner.HasSpellsFile(folder)
        || File.Exists(Path.Combine(folder, "eqgame.exe"))
        || File.Exists(Path.Combine(folder, "eqclient.ini"));

    private void Remove(string fileName)
    {
        if (_directory is null)
        {
            return;
        }

        try
        {
            File.Delete(Path.Combine(_directory, fileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It is read again (and dropped again) next start.
        }
    }

    /// <summary>
    /// Reads one output file. Returns false when the name is not an output file, the file cannot be read, or what is stored is
    /// already as new as the file.
    /// </summary>
    public bool Import(string filePath, string? fromPc = null)
    {
        if (!CharacterOutputParser.TryParseFileName(filePath, out OutputFileInfo info))
        {
            return false;
        }

        DateTime stamp;
        List<string> lines;
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            stamp = File.GetLastWriteTimeUtc(filePath);
            lines = ReadLines(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        CharacterData saveCharacter;
        SharedBankData? saveShared = null;

        lock (_gate)
        {
            string key = Key(info.Name, info.Server);
            if (!_characters.TryGetValue(key, out CharacterData? data))
            {
                data = new CharacterData { Name = info.Name, Server = info.Server };
            }

            bool changed = false;
            switch (info.Kind)
            {
                case OutputFileKind.Inventory:
                    if (data.InventoryUtc is { } have && have >= stamp)
                    {
                        break;
                    }

                    InventoryFile file = CharacterOutputParser.ParseInventory(lines);
                    data.Inventory = file.Items.Where(i => !i.IsShared).ToList();
                    data.KeyRing = file.KeyRing.ToList();
                    data.InventoryUtc = stamp;
                    changed = true;

                    List<InventoryItem> sharedItems = file.Items.Where(i => i.IsShared).ToList();
                    if (!_shared.TryGetValue(info.Server, out SharedBankData? shared) || shared.CapturedUtc is null || shared.CapturedUtc < stamp)
                    {
                        saveShared = new SharedBankData { Server = info.Server, CapturedUtc = stamp, FromCharacter = info.Name, Items = sharedItems };
                        _shared[info.Server] = saveShared;
                    }

                    break;

                case OutputFileKind.Spellbook:
                    if (data.SpellbookUtc is { } haveBook && haveBook >= stamp)
                    {
                        break;
                    }

                    data.Spellbook = CharacterOutputParser.ParseSpellList(lines);
                    data.SpellbookUtc = stamp;
                    changed = true;
                    break;

                default:
                    if (data.MissingUtc is { } haveMissing && haveMissing >= stamp)
                    {
                        break;
                    }

                    data.Missing = CharacterOutputParser.ParseSpellList(lines);
                    data.MissingUtc = stamp;
                    changed = true;
                    break;
            }

            if (!changed)
            {
                return false;
            }

            if (info.ClassCode.Length > 0)
            {
                data.ClassCode = info.ClassCode;
            }

            data.FromPc = fromPc ?? string.Empty;

            _characters[key] = data;
            saveCharacter = data;
        }

        Save(saveCharacter);
        if (saveShared is not null)
        {
            Save(saveShared);
        }

        Changed?.Invoke();
        return true;
    }

    private static string Key(string name, string server) => $"{name}_{server}";

    private static List<string> ReadLines(string path)
    {
        // EverQuest may still hold the file open; share it instead of failing.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add(line);
        }

        return lines;
    }

    private void Load()
    {
        if (_directory is null || !Directory.Exists(_directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                string json = File.ReadAllText(file);
                if (Path.GetFileName(file).StartsWith(SharedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    SharedBankData? shared = JsonSerializer.Deserialize<SharedBankData>(json, JsonOptions);
                    if (shared is not null && shared.Server.Length > 0)
                    {
                        shared.Items ??= new List<InventoryItem>();
                        _shared[shared.Server] = shared;
                    }
                }
                else
                {
                    CharacterData? data = JsonSerializer.Deserialize<CharacterData>(json, JsonOptions);
                    if (data is not null && data.Name.Length > 0)
                    {
                        data.Inventory ??= new List<InventoryItem>();
                        data.KeyRing ??= new List<KeyRingEntry>();
                        data.Spellbook ??= new List<SpellListEntry>();
                        data.Missing ??= new List<SpellListEntry>();
                        _characters[Key(data.Name, data.Server)] = data;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A damaged file is skipped; the next /outputfile read replaces it.
            }
        }
    }

    private void Save(CharacterData data) => Write($"{Key(data.Name, data.Server)}.json", JsonSerializer.Serialize(data, JsonOptions));

    private void Save(SharedBankData data) => Write($"{SharedPrefix}{data.Server}.json", JsonSerializer.Serialize(data, JsonOptions));

    private void Write(string fileName, string json)
    {
        if (_directory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, fileName);
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The data stays in memory and is read from the game folder again next time.
        }
    }
}
