using System.Collections.Immutable;
using UiPath.Caching.Locking;

namespace UiPath.Caching;

internal sealed class MemorySetCache(
    string cacheName,
    IMemoryCache memoryCache,
    ISerializerProxy<RedisValue> serializer,
    ILocalLock localLock,
    IMemoryCacheOptions memoryCacheOptions)
{
    private readonly bool _trackSize = memoryCacheOptions.SizeLimit.HasValue;
    private readonly string _localLockKeyPrefix = cacheName + ":";

    private sealed record Snapshot(ImmutableHashSet<RedisValue> Members, DateTimeOffset? Expiration);

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
        contains = snapshot.Members.Contains(serializer.Serialize(item));
        return true;
    }

    public bool ContainsKey(string key) => TryGetSnapshot(key, out _);

    public void Remove(string key) => memoryCache.Remove(key);

    public async ValueTask ReplaceAsync<T>(string key, IEnumerable<T> members, DateTimeOffset? expiration, CancellationToken token)
    {
        var set = members.Select(m => serializer.Serialize(m)).ToImmutableHashSet();
        using (await localLock.AcquireAsync(_localLockKeyPrefix + key, token).ConfigureAwait(false))
        {
            StoreSnapshot(key, new Snapshot(set, expiration));
        }
    }

    public async ValueTask AddAsync<T>(string key, IEnumerable<T> items, CancellationToken token)
    {
        var values = items.Select(i => serializer.Serialize(i)).ToArray();
        if (values.Length == 0)
        {
            return;
        }
        using (await localLock.AcquireAsync(_localLockKeyPrefix + key, token).ConfigureAwait(false))
        {
            if (TryGetSnapshot(key, out var snapshot))
            {
                StoreSnapshot(key, snapshot with { Members = snapshot.Members.Union(values) });
            }
        }
    }

    public async ValueTask<long> AddAsync<T>(string key, IEnumerable<T> items, DateTimeOffset? expiration, CancellationToken token)
    {
        var values = items.Select(i => serializer.Serialize(i)).ToArray();
        if (values.Length == 0)
        {
            return 0;
        }
        using (await localLock.AcquireAsync(_localLockKeyPrefix + key, token).ConfigureAwait(false))
        {
            if (expiration.HasValue && expiration.Value <= DateTimeOffset.UtcNow)
            {
                memoryCache.Remove(key);
                return 0;
            }
            var members = TryGetSnapshot(key, out var snapshot) ? snapshot.Members : ImmutableHashSet<RedisValue>.Empty;
            var updated = members.Union(values);
            StoreSnapshot(key, new Snapshot(updated, expiration));
            return updated.Count - members.Count;
        }
    }

    public async ValueTask<long> RemoveAsync<T>(string key, IEnumerable<T> items, CancellationToken token)
    {
        var values = items.Select(i => serializer.Serialize(i)).ToArray();
        if (values.Length == 0)
        {
            return 0;
        }
        using (await localLock.AcquireAsync(_localLockKeyPrefix + key, token).ConfigureAwait(false))
        {
            if (!TryGetSnapshot(key, out var snapshot))
            {
                return 0;
            }
            var updated = snapshot.Members.Except(values);
            StoreSnapshot(key, snapshot with { Members = updated });
            return snapshot.Members.Count - updated.Count;
        }
    }

    public async ValueTask<bool> RemoveKeyAsync(string key, CancellationToken token)
    {
        using (await localLock.AcquireAsync(_localLockKeyPrefix + key, token).ConfigureAwait(false))
        {
            if (!TryGetSnapshot(key, out _))
            {
                return false;
            }
            memoryCache.Remove(key);
            return true;
        }
    }

    public async ValueTask<IReadOnlyCollection<T?>> PopAsync<T>(string key, long count, CancellationToken token)
    {
        if (count <= 0)
        {
            return [];
        }
        using (await localLock.AcquireAsync(_localLockKeyPrefix + key, token).ConfigureAwait(false))
        {
            if (!TryGetSnapshot(key, out var snapshot) || snapshot.Members.IsEmpty)
            {
                return [];
            }
            var pool = snapshot.Members.ToArray();
            var take = (int)Math.Min(count, pool.Length);
            var picked = new RedisValue[take];
            for (var i = 0; i < take; i++)
            {
                var j = Random.Shared.Next(i, pool.Length);
                (pool[i], pool[j]) = (pool[j], pool[i]);
                picked[i] = pool[i];
            }
            StoreSnapshot(key, snapshot with { Members = snapshot.Members.Except(picked) });
            return Deserialize<T>(picked);
        }
    }

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

    private IReadOnlyCollection<T?> Deserialize<T>(IReadOnlyCollection<RedisValue> values)
    {
        if (values.Count == 0)
        {
            return [];
        }
        var list = new List<T?>(values.Count);
        foreach (var value in values)
        {
            if (value.IsNull)
            {
                continue;
            }
            list.Add(serializer.Deserialize<T>(value));
        }
        return list;
    }
}
