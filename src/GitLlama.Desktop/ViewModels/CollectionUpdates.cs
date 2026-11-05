using System.Collections.ObjectModel;

namespace GitLlama.Desktop.ViewModels;

internal static class CollectionUpdates
{
    internal static void Sync<T>(ObservableCollection<T> target, IEnumerable<T> values, Func<T, string> key)
    {
        var desired = values.ToArray();
        if (target.SequenceEqual(desired))
            return;

        var desiredKeys = desired.Select(key).ToHashSet(StringComparer.Ordinal);
        // Small edits retain individual notifications and selection. Large snapshots must not make
        // the staged/unstaged projections and bound controls rebuild thousands of times.
        if (target is BatchObservableCollection<T> batch)
        {
            var existing = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var item in target)
                existing.TryAdd(key(item), item);
            // Count actual removals, insertions, replacements and reordering. An insertion/removal
            // in the middle shifts many indexes but must not turn one edit into a full list reset.
            var changed = target.Count(item => !desiredKeys.Contains(key(item)))
                + desired.Count(item => !existing.TryGetValue(key(item), out var retained)
                    || !EqualityComparer<T>.Default.Equals(retained, item));
            changed += target.Where(item => desiredKeys.Contains(key(item))).Select(key)
                .Zip(desired.Where(item => existing.ContainsKey(key(item))).Select(key),
                    (before, after) => before == after ? 0 : 1).Sum();
            if (changed > 128)
            {
                for (var i = 0; i < desired.Length; i++)
                    if (existing.TryGetValue(key(desired[i]), out var retained)
                        && EqualityComparer<T>.Default.Equals(retained, desired[i]))
                        desired[i] = retained;
                batch.ReplaceAll(desired);
                return;
            }
        }

        // Remove departed rows before reconciling positions. Moving their successors forward
        // first makes virtualized lists follow those moves and jump to the end while staging.
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!desiredKeys.Contains(key(target[index])))
                target.RemoveAt(index);
        }

        for (var index = 0; index < desired.Length; index++)
        {
            var found = -1;
            for (var candidate = index; candidate < target.Count; candidate++)
            {
                if (key(target[candidate]) == key(desired[index]))
                {
                    found = candidate;
                    break;
                }
            }

            if (found < 0)
            {
                target.Insert(index, desired[index]);
            }
            else
            {
                if (found != index)
                {
                    target.Move(found, index);
                }

                if (!EqualityComparer<T>.Default.Equals(target[index], desired[index]))
                {
                    target[index] = desired[index];
                }
            }
        }

        while (target.Count > desired.Length)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
