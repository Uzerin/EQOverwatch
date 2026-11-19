using System.Collections.Concurrent;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Services;

/// <summary>
/// Notes the files EverQuest says it has written ("Outputfile Complete: Stackprime_frostreaver-Inventory.txt"), so the app can read them
/// straight away. <see cref="TakeAll"/> hands them over (on the UI tick) as file names, never paths.
/// </summary>
public sealed class OutputFileWatch : ITriggerLineSink
{
    public const string Prefix = "Outputfile Complete: ";

    private readonly ConcurrentQueue<(string Character, string FileName)> _done = new();

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (!message.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return;
        }

        string name = message[Prefix.Length..].Trim().ToString();
        if (FileNameOnly(name) is { } file)
        {
            _done.Enqueue((characterName, file));
        }
    }

    /// <summary>The file name alone, or null when the line names a path or nothing.</summary>
    public static string? FileNameOnly(string name) =>
        name.Length == 0 || name.Length > 200 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..", StringComparison.Ordinal)
            ? null
            : name;

    /// <summary>The files written since the last call, oldest first.</summary>
    public IReadOnlyList<(string Character, string FileName)> TakeAll()
    {
        var list = new List<(string, string)>();
        while (_done.TryDequeue(out (string, string) item))
        {
            list.Add(item);
        }

        return list;
    }
}
