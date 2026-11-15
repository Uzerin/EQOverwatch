using System.Collections.ObjectModel;

namespace EQOverwatch.App.Presentation;

/// <summary>
/// Keeps an <see cref="ObservableCollection{T}"/> of view models in step with the latest snapshot list without rebuilding it.
/// Existing view models are reused (so bars keep their bindings and nothing flickers), stale ones are removed, new ones are
/// inserted, and order changes are applied with <c>Move</c>.
/// </summary>
public static class ObservableSync
{
    public static void Sync<TViewModel, TSnapshot>(
        ObservableCollection<TViewModel> target,
        IReadOnlyList<TSnapshot> desired,
        Func<TSnapshot, long> snapshotKey,
        Func<TViewModel, long> viewModelKey,
        Func<TSnapshot, TViewModel> create,
        Action<TViewModel, TSnapshot> update)
    {
        var keep = new HashSet<long>();
        foreach (TSnapshot snapshot in desired)
        {
            keep.Add(snapshotKey(snapshot));
        }

        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(viewModelKey(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < desired.Count; i++)
        {
            TSnapshot snapshot = desired[i];
            long key = snapshotKey(snapshot);

            if (i < target.Count && viewModelKey(target[i]) == key)
            {
                update(target[i], snapshot);
                continue;
            }

            int existing = -1;
            for (int j = i + 1; j < target.Count; j++)
            {
                if (viewModelKey(target[j]) == key)
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
            {
                target.Move(existing, i);
                update(target[i], snapshot);
            }
            else
            {
                TViewModel created = create(snapshot);
                update(created, snapshot);
                target.Insert(i, created);
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="target"/> equal to <paramref name="desired"/> (compared by value) item by item, never clearing it, so a
    /// drop-down bound to it keeps its selection while the items that stay are untouched. <paramref name="desired"/> must not repeat items.
    /// Returns true when anything changed.
    /// </summary>
    public static bool SyncValues<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
    {
        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        if (target.SequenceEqual(desired, comparer))
        {
            return false;
        }

        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i], comparer))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && comparer.Equals(target[i], desired[i]))
            {
                continue;
            }

            int existing = -1;
            for (int j = i + 1; j < target.Count; j++)
            {
                if (comparer.Equals(target[j], desired[i]))
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
            {
                target.Move(existing, i);
            }
            else
            {
                target.Insert(i, desired[i]);
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1); // only duplicates can be left over
        }

        return true;
    }
}
