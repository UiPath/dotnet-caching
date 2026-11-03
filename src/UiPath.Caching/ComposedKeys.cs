using System.Collections;

namespace UiPath.Caching;

/// <summary>A batch input with the key strategy applied as it is read, and the way back to the caller's keys.</summary>
internal class ComposedKeys<TItem>(IEnumerable<TItem> source, Func<TItem, CacheKey> keyOf, Func<CacheKey, CacheKey> compose, Func<TItem, CacheKey, TItem> rekey) : IEnumerable<TItem>
{
    private Dictionary<string, CacheKey>? _originals;

    /// <summary>The caller's key of the item read last.</summary>
    internal CacheKey Original { get; private set; }

    /// <summary>Set by a batch runner that gives back the caller's keys itself, chunk by chunk, before it reads the input.</summary>
    internal bool RestoredPerChunk { get; set; }

    public IEnumerator<TItem> GetEnumerator()
    {
        foreach (var item in source)
        {
            var original = keyOf(item);
            var composed = compose(original);
            Original = original;
            if (!RestoredPerChunk)
            {
                _originals?.TryAdd(composed.Name, original);
            }

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
        if (failed.Count == 0 || RestoredPerChunk)
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

    /// <summary>Every caller key of a counted input, in its order.</summary>
    internal List<CacheKey> Originals() => source.Select(keyOf).ToList();

    /// <summary>The caller's keys behind one chunk's failed keys; a key the chunk folded gives back each caller key folded into it.</summary>
    /// <remarks>Two caller keys that compose to one stored key in different chunks are told apart here, where the chunk is still known.</remarks>
    internal List<CacheKey>? RestoreChunk(List<CacheKey>? failed, TItem[] chunk, CacheKey[] originals, int count)
    {
        if (failed is null || failed.Count == 0)
        {
            return failed;
        }

        var names = new HashSet<CacheKey>(failed);
        var seen = new HashSet<CacheKey>();
        var restored = new List<CacheKey>(failed.Count);
        var matched = new HashSet<CacheKey>();
        for (var i = 0; i < count; i++)
        {
            var key = keyOf(chunk[i]);
            if (names.Contains(key))
            {
                matched.Add(key);
                if (seen.Add(originals[i]))
                {
                    restored.Add(originals[i]);
                }
            }
        }

        // A failed key that matches no item of the chunk is still a failure, given back as it came.
        if (matched.Count < names.Count)
        {
            restored.AddRange(failed.Where(k => !matched.Contains(k) && seen.Add(k)));
        }

        return restored;
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
