using System.Collections.Immutable;

namespace UiPath.Caching;

internal sealed class MemorySetCache(
    IMemoryCache memoryCache,
    ISerializerProxy<byte[]> serializer,
    IMemoryCacheOptions memoryCacheOptions,
    TimeProvider clock)
{

    private static readonly ImmutableHashSet<byte[]> EmptyMembers =
        ImmutableHashSet.Create<byte[]>(ByteArrayEqualityComparer.Instance);
    private readonly bool _trackSize = memoryCacheOptions.SizeLimit.HasValue;
    private readonly StripedLocks _locks = new();

    public bool TryGetMembers<T>(string key, [NotNullWhen(true)] out IReadOnlyCollection<T?>? members)
    {
        if (!TryGetSnapshot(key, out var snapshot))
        {
            members = null;
            return false;
        }
        members = Deserialize<T>(snapshot.Members);
        return true;
    }

    public bool TryGetCount(string key, out long count)
    {
        if (!TryGetSnapshot(key, out var snapshot))
        {
            count = 0;
            return false;
        }
        count = snapshot.Members.Count;
        return true;
    }

    public bool TryContainsItem<T>(string key, T item, out bool contains)
    {
        if (!TryGetSnapshot(key, out var snapshot))
        {
            contains = false;
            return false;
        }
        contains = snapshot.Members.Contains(serializer.Serialize(item)!);
        return true;
    }

    public bool ContainsKey(string key) => TryGetSnapshot(key, out _);

    public void Remove(string key) => memoryCache.Remove(key);

    public ValueTask ReplaceAsync<T>(string key, IEnumerable<T> members, DateTimeOffset? expiration, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(token);
        }

        var set = members.Select(m => Owned(serializer.Serialize(m)!)).ToImmutableHashSet(ByteArrayEqualityComparer.Instance);
        lock (_locks.For(key))
        {
            StoreSnapshot(key, new Snapshot(set, expiration));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask AddAsync<T>(string key, IEnumerable<T> items, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(token);
        }

        var values = items.Select(i => Owned(serializer.Serialize(i)!)).ToArray();
        if (values.Length == 0)
        {
            return ValueTask.CompletedTask;
        }

        lock (_locks.For(key))
        {
            if (TryGetSnapshot(key, out var snapshot))
            {
                StoreSnapshot(key, snapshot with { Members = snapshot.Members.Union(values) });
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<long> AddAsync<T>(string key, IEnumerable<T> items, DateTimeOffset? expiration, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<long>(token);
        }

        var values = items.Select(i => Owned(serializer.Serialize(i)!)).ToArray();
        if (values.Length == 0)
        {
            return new ValueTask<long>(0);
        }

        lock (_locks.For(key))
        {
            if (expiration.HasValue && expiration.Value <= clock.GetUtcNow())
            {
                memoryCache.Remove(key);
                return new ValueTask<long>(0);
            }
            var members = TryGetSnapshot(key, out var snapshot) ? snapshot.Members : EmptyMembers;
            var updated = members.Union(values);
            StoreSnapshot(key, new Snapshot(updated, expiration));
            return new ValueTask<long>(updated.Count - members.Count);
        }
    }

    public ValueTask<long> RemoveAsync<T>(string key, IEnumerable<T> items, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<long>(token);
        }

        var values = items.Select(i => serializer.Serialize(i)!).ToArray();
        if (values.Length == 0)
        {
            return new ValueTask<long>(0);
        }

        lock (_locks.For(key))
        {
            if (!TryGetSnapshot(key, out var snapshot))
            {
                return new ValueTask<long>(0);
            }
            var updated = snapshot.Members.Except(values);
            StoreSnapshot(key, snapshot with { Members = updated });
            return new ValueTask<long>(snapshot.Members.Count - updated.Count);
        }
    }

    public ValueTask<bool> RemoveKeyAsync(string key, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<bool>(token);
        }

        lock (_locks.For(key))
        {
            if (!TryGetSnapshot(key, out _))
            {
                return new ValueTask<bool>(false);
            }
            memoryCache.Remove(key);
            return new ValueTask<bool>(true);
        }
    }

    public ValueTask<IReadOnlyCollection<T?>> PopAsync<T>(string key, long count, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<IReadOnlyCollection<T?>>(token);
        }

        if (count <= 0)
        {
            return new ValueTask<IReadOnlyCollection<T?>>([]);
        }

        byte[][] picked;
        lock (_locks.For(key))
        {
            if (!TryGetSnapshot(key, out var snapshot) || snapshot.Members.IsEmpty)
            {
                return new ValueTask<IReadOnlyCollection<T?>>([]);
            }
            var pool = snapshot.Members.ToArray();
            var take = (int)Math.Min(count, pool.Length);
            picked = new byte[take][];
            for (var i = 0; i < take; i++)
            {
                var j = Random.Shared.Next(i, pool.Length);
                (pool[i], pool[j]) = (pool[j], pool[i]);
                picked[i] = pool[i];
            }
            StoreSnapshot(key, snapshot with { Members = snapshot.Members.Except(picked) });
        }

        // Outside the stripe: the picked members are this call's alone, and a large pop must not hold up the keys that share it.
        return new ValueTask<IReadOnlyCollection<T?>>(Deserialize<T>(picked));
    }

    /// <summary>
    /// A passthrough serializer such as <see cref="RawByteSerializerProxy"/> hands back the caller's
    /// own array. The snapshot hashes its members, so a caller mutating that array afterwards would
    /// change an element's hash while it sits in the set. Only the paths that store need this; the
    /// lookup paths may compare against the caller's array directly.
    /// </summary>
    private static byte[] Owned(byte[] value) => value.Length == 0 ? value : (byte[])value.Clone();

    private bool TryGetSnapshot(string key, [NotNullWhen(true)] out Snapshot? snapshot) =>
        memoryCache.TryGetValue(key, out snapshot) && snapshot is not null;

    private void StoreSnapshot(string key, Snapshot snapshot)
    {
        if (snapshot.Members.IsEmpty)
        {
            memoryCache.Remove(key);
            return;
        }
        var options = new MemoryCacheEntryOptions();
        if (snapshot.Expiration.HasValue)
        {
            options.SetAbsoluteExpiration(snapshot.Expiration.Value);
        }
        if (_trackSize)
        {
            options.SetSize(1);
        }
        memoryCache.Set(key, snapshot, options);
    }

    private List<T?> Deserialize<T>(IReadOnlyCollection<byte[]> values)
    {
        if (values.Count == 0)
        {
            return [];
        }
        var list = new List<T?>(values.Count);
        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }
            list.Add(serializer.Deserialize<T>(value));
        }
        return list;
    }

    private sealed record Snapshot(ImmutableHashSet<byte[]> Members, DateTimeOffset? Expiration);

    /// <summary>A fixed set of locks picked by the key's hash, so writers of one key exclude each other and writers of other keys rarely meet. Every section under one holds no await.</summary>
    private sealed class StripedLocks
    {
        // A power of two, so the hash picks a stripe with a mask; 128 keeps a collision between two hot keys rare and costs about 3 KB per cache.
        private const int Stripes = 128;
        private readonly Lock[] _locks = Create();

        public Lock For(string key) => _locks[(uint)key.GetHashCode() & (Stripes - 1)];

        private static Lock[] Create()
        {
            var locks = new Lock[Stripes];
            for (var i = 0; i < locks.Length; i++)
            {
                locks[i] = new Lock();
            }

            return locks;
        }
    }
}
