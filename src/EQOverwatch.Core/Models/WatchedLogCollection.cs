using System.Collections.ObjectModel;
using System.ComponentModel;

namespace EQOverwatch.Core.Models;

/// <summary>
/// Observable list of watched logs that enforces two invariants:
/// at most one log has <see cref="WatchedLogConfig.IsPrimaryDps"/> set, at most one log holds each non-zero
/// <see cref="WatchedLogConfig.ChChainPosition"/>, and a file path appears only once.
/// Intended to be mutated on the UI thread (it raises <see cref="ObservableCollection{T}.CollectionChanged"/>).
/// </summary>
public sealed class WatchedLogCollection : ObservableCollection<WatchedLogConfig>
{
    private bool _enforcing;

    public WatchedLogConfig? PrimaryDpsLog => this.FirstOrDefault(l => l.IsPrimaryDps);

    /// <summary>Adds a log by path, or returns the existing entry if that path is already watched.</summary>
    public WatchedLogConfig AddFile(string filePath)
    {
        string full = Path.GetFullPath(filePath);
        WatchedLogConfig? existing = this.FirstOrDefault(l => PathEquals(l.FilePath, full));
        if (existing is not null)
        {
            return existing;
        }

        var config = new WatchedLogConfig(full);
        Add(config);
        return config;
    }

    protected override void InsertItem(int index, WatchedLogConfig item)
    {
        ThrowIfDuplicate(item, ignore: null);
        base.InsertItem(index, item);
        Attach(item);
    }

    protected override void SetItem(int index, WatchedLogConfig item)
    {
        WatchedLogConfig old = this[index];
        ThrowIfDuplicate(item, ignore: old);
        Detach(old);
        base.SetItem(index, item);
        Attach(item);
    }

    protected override void RemoveItem(int index)
    {
        Detach(this[index]);
        base.RemoveItem(index);
    }

    protected override void ClearItems()
    {
        foreach (WatchedLogConfig item in this)
        {
            Detach(item);
        }

        base.ClearItems();
    }

    private void ThrowIfDuplicate(WatchedLogConfig item, WatchedLogConfig? ignore)
    {
        foreach (WatchedLogConfig other in this)
        {
            if (!ReferenceEquals(other, ignore) && PathEquals(other.FilePath, item.FilePath))
            {
                throw new ArgumentException($"'{item.FilePath}' is already being watched.", nameof(item));
            }
        }
    }

    private void Attach(WatchedLogConfig item)
    {
        item.PropertyChanged += OnItemPropertyChanged;
        if (item.IsPrimaryDps)
        {
            EnforceSinglePrimary(item);
        }

        if (item.ChChainPosition > 0)
        {
            EnforceUniquePosition(item);
        }
    }

    private void Detach(WatchedLogConfig item) => item.PropertyChanged -= OnItemPropertyChanged;

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WatchedLogConfig.IsPrimaryDps)
            && sender is WatchedLogConfig changed
            && changed.IsPrimaryDps)
        {
            EnforceSinglePrimary(changed);
        }
        else if (e.PropertyName == nameof(WatchedLogConfig.ChChainPosition)
                 && sender is WatchedLogConfig positioned
                 && positioned.ChChainPosition > 0)
        {
            EnforceUniquePosition(positioned);
        }
    }

    /// <summary>Two characters cannot hold the same chain slot: the newest assignment wins and the other falls back to "None".</summary>
    private void EnforceUniquePosition(WatchedLogConfig winner)
    {
        if (_enforcing)
        {
            return;
        }

        _enforcing = true;
        try
        {
            foreach (WatchedLogConfig other in this)
            {
                if (!ReferenceEquals(other, winner) && other.ChChainPosition == winner.ChChainPosition)
                {
                    other.ChChainPosition = 0;
                }
            }
        }
        finally
        {
            _enforcing = false;
        }
    }

    private void EnforceSinglePrimary(WatchedLogConfig winner)
    {
        if (_enforcing)
        {
            return;
        }

        _enforcing = true;
        try
        {
            foreach (WatchedLogConfig other in this)
            {
                if (!ReferenceEquals(other, winner) && other.IsPrimaryDps)
                {
                    other.IsPrimaryDps = false;
                }
            }
        }
        finally
        {
            _enforcing = false;
        }
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
