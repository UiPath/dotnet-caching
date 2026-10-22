using System.Collections;

namespace UiPath.Caching;

/// <summary>A batch input with the key strategy applied as it is read, and the way back to the caller's keys.</summary>
internal class ComposedKeys<TItem>(IEnumerable<TItem> source, Func<TItem, CacheKey> keyOf, Func<CacheKey, CacheKey> compose, Func<TItem, CacheKey, TItem> rekey) : IEnumerable<TItem>
{
    private Dictionary<string, CacheKey>? _originals;

    public IEnumerator<TItem> GetEnumerator()
    {
        foreach (var item in source)
        {
            var original = keyOf(item);
            var composed = compose(original);
            _originals?.TryAdd(composed.Name, original);

            yield return rekey(item, composed);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal static ComposedKeys<TItem> Create(IEnumerable<TItem> source, Func<TItem, CacheKey> keyOf, Func<CacheKey, CacheKey> compose, Func<TItem, CacheKey, TItem> rekey)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.TryGetNonEnumeratedCount(out var count))
        {
            return new Counted(source, keyOf, compose, rekey, count);
        }

        // A stream cannot be read twice, so its keys are remembered as they pass.
        return new ComposedKeys<TItem>(source, keyOf, compose, rekey) { _originals = new Dictionary<string, CacheKey>(StringComparer.Ordinal) };
    }

    internal IReadOnlyList<CacheKey> Restore(IReadOnlyList<CacheKey> failed)
    {
        if (failed.Count == 0)
        {
            return failed;
        }

        if (_originals is not null)
        {
            return failed.Select(k => _originals.TryGetValue(k.Name, out var original) ? original : k).ToList();
        }

        var names = new HashSet<string>(failed.Select(k => k.Name), StringComparer.Ordinal);
        return source.Select(keyOf).Where(k => names.Contains(compose(k).Name)).ToList();
    }

    // A read-only ICollection<T>, the shape TryGetNonEnumeratedCount reads, so the batch still knows its size.
    private sealed class Counted(IEnumerable<TItem> source, Func<TItem, CacheKey> keyOf, Func<CacheKey, CacheKey> compose, Func<TItem, CacheKey, TItem> rekey, int count)
        : ComposedKeys<TItem>(source, keyOf, compose, rekey), ICollection<TItem>
    {
        public int Count => count;

        public bool IsReadOnly => true;

        public void Add(TItem item) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Remove(TItem item) => throw new NotSupportedException();

        // Not LINQ's Contains: it would call this one back on an ICollection<T>. A loop, not Any, to allocate no closure.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3267:Loops should be simplified with LINQ expressions", Justification = "A closure per call; LINQ's Contains recurses here.")]
        public bool Contains(TItem item)
        {
            foreach (var each in this)
            {
                if (EqualityComparer<TItem>.Default.Equals(each, item))
                {
                    return true;
                }
            }

            return false;
        }

        public void CopyTo(TItem[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(arrayIndex, array.Length - count);
            foreach (var item in this)
            {
                array[arrayIndex++] = item;
            }
        }
    }
}
