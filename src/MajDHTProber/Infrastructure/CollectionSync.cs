using System.Collections.Generic;
using System.Collections.ObjectModel;

#nullable enable

namespace MajDHTProber.Infrastructure
{
    /// <summary>Updates bound collections in place, so list selection and scroll position survive refreshes.</summary>
    public static class CollectionSync
    {
        /// <summary>Makes a collection equal to a desired sequence using moves, inserts and removals of the differences only.</summary>
        /// <typeparam name="T">The item type, compared by reference.</typeparam>
        /// <param name="target">The bound collection.</param>
        /// <param name="desired">The desired items in order.</param>
        /// <returns>Whether the collection changed.</returns>
        public static bool Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
            where T : class
        {
            var changed = false;
            for (var index = 0; index < desired.Count; index++)
            {
                var item = desired[index];
                if (index < target.Count && ReferenceEquals(target[index], item))
                {
                    continue;
                }
                var existing = IndexOf(target, item, index + 1);
                if (existing >= 0)
                {
                    target.Move(existing, index);
                }
                else
                {
                    target.Insert(index, item);
                }
                changed = true;
            }
            while (target.Count > desired.Count)
            {
                target.RemoveAt(target.Count - 1);
                changed = true;
            }
            return changed;
        }

        /// <summary>Finds an item by reference from a start index.</summary>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="items">The collection.</param>
        /// <param name="item">The item.</param>
        /// <param name="start">The first index searched.</param>
        /// <returns>The index, or -1.</returns>
        private static int IndexOf<T>(ObservableCollection<T> items, T item, int start)
            where T : class
        {
            for (var index = start; index < items.Count; index++)
            {
                if (ReferenceEquals(items[index], item))
                {
                    return index;
                }
            }
            return -1;
        }
    }
}
