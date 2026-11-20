using System.Runtime.ExceptionServices;

namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ICacheEntry<T>? local = null;
        var probe = token.IsCancellationRequested ? LocalProbe.Unavailable : ProbeLocal<T>(cacheKey, token, out local);
        if (probe == LocalProbe.Hit)
        {
            return new ValueTask<T?>(local!.Value);
        }

        if (probe == LocalProbe.Miss && !_sharedInner)
        {
            return new ValueTask<T?>(default(T?));
        }

        policy ??= _defaultPolicy;
        return GetInnerAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_multiLayerCacheOptions.DefaultExpiration), token), policy);
    }

    public ValueTask<T?> GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (TryGetLocal<T>(cacheKey, token, out var local))
        {
            return new ValueTask<T?>(local.Value);
        }

        return GetAsync<T>(new CacheKey(cacheKey), policy, token);
    }

    public ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var options = Array.ConvertAll(cacheKeys, k => _entryBuilder.BuildEntryOptions<T>(k, default, token));
        return GetInnerAsync<T>(options, policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ICacheEntry<T?>? local = null;
        var probe = token.IsCancellationRequested ? LocalProbe.Unavailable : ProbeLocal<T?>(cacheKey, token, out local);
        if (probe == LocalProbe.Hit)
        {
            return new ValueTask<ICacheEntry<T?>>(local!);
        }

        if (probe == LocalProbe.Miss && !_sharedInner)
        {
            return new ValueTask<ICacheEntry<T?>>(NullCache.NullCacheEntry<T?>.Instance);
        }

        return GetCacheEntryMissAsync<T>(cacheKey, policy, token);
    }

    public ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T?>(cacheKey, token, out var local))
        {
            return new ValueTask<ICacheEntry<T?>>(local);
        }

        return GetCacheEntryAsync<T>(new CacheKey(cacheKey), policy, token);
    }

    public async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var options = Array.ConvertAll(cacheKeys, k => _entryBuilder.BuildEntryOptions<T>(k, default, token));
        return await GetCacheEntriesInnerAsync<T>(options, policy, token).ConfigureAwait(false);
    }

    [OverloadResolutionPriority(1)]
    public async ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T>(cacheKey, token, out _))
        {
            return true;
        }

        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);
        try
        {
            return _memoryCache.TryGetValue(cacheEntryOptions.CacheKey.Name, out _) || await _innerCache.ContainsAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cacheEntryOptions.Token.IsCancellationRequested))
        {
            LogInnerCacheContainsError(ex, Logged(cacheKey, typeof(T)));
            return false;
        }
    }

    public ValueTask<bool> ContainsAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return !token.IsCancellationRequested && TryGetLocal<T>(cacheKey, token, out _)
            ? new ValueTask<bool>(true)
            : ContainsAsync<T>(new CacheKey(cacheKey), token);
    }

    public async ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);
        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey.Name, out var value)
            ? value?.Expiration.Subtract(_clock.GetUtcNow())
            : await _innerCache.TimeToLiveAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return !token.IsCancellationRequested && TryGetLocal<T>(cacheKey, token, out var local)
            ? new ValueTask<TimeSpan?>(local.Expiration.Subtract(_clock.GetUtcNow()))
            : TimeToLiveAsync<T>(new CacheKey(cacheKey), token);
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);

        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey.Name, out var value)
            ? value?.Expiration
            : await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return !token.IsCancellationRequested && TryGetLocal<T>(cacheKey, token, out var local)
            ? new ValueTask<DateTimeOffset?>(local.Expiration)
            : ExpireTimeAsync<T>(new CacheKey(cacheKey), token);
    }

    private static async ValueTask<T?> ValueOfAsync<T>(ValueTask<object?> entry) =>
        ((ICacheEntry<T?>)(await entry.ConfigureAwait(false))!).Value;

    /// <summary>Reports a failure through the task, as an async method does, for the methods that are not async.</summary>
    private static async ValueTask<T?> FailedAsync<T>(Exception failure)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        ExceptionDispatchInfo.Throw(failure);
        return default;
    }

    private async ValueTask<KeyValuePair<CacheKey, T?>[]> GetInnerAsync<T>(CacheEntryOptions[] options, CachePolicy policy, CancellationToken token = default)
    {
        List<KeyValuePair<CacheKey, T?>> results = [];
        List<CacheEntryOptions> cacheEntriesToFetch = [];
        foreach (var option in options)
        {
            if (_memoryCache.TryGetValue<ICacheEntry<T>>(option.CacheKey.Name, out var entry))
            {
                LogFoundLocal(Logged(option, typeof(T)));
                if (_connectionState.IsConnected)
                {
                    MarkRead(entry);
                    results.Add(new KeyValuePair<CacheKey, T?>(option.CacheKey, entry!.Value));
                }
                else if (_useLocalOnlyWhenDisconnected)
                {
                    LogUsingPrimaryOnlyWhenDisconnected(Logged(option, typeof(T)));
                    MarkRead(entry);
                    results.Add(new KeyValuePair<CacheKey, T?>(option.CacheKey, entry!.Value));
                }
                else
                {
                    LogReturningDefaultDisconnected(Logged(option, typeof(T)));
                    RemoveLocal(option.CacheKey.Name);
                }
            }
            else
            {
                cacheEntriesToFetch.Add(option);
            }
        }

        var keys = cacheEntriesToFetch.Select(c => c.CacheKey).ToArray();
        if (keys.Length == 0)
        {
            return results.ToArray();
        }

        var marks = MarkInnerReads(keys, cacheEntriesToFetch[0].TopicKey);
        var fetched = await _innerCache.GetCacheEntriesAsync<T>(keys, policy, token).ConfigureAwait(false);

        for (int i = 0; i < keys.Length; i++)
        {
            var entry = fetched[i].Value;
            var key = fetched[i].Key;
            results.Add(new KeyValuePair<CacheKey, T?>(key, entry.Value));

            if (!entry.Found)
            {
                continue;
            }

            LogFoundInnerCacheCopy(LoggedComposed(key, typeof(T)));
            var option = cacheEntriesToFetch[i];
            option.Expiration = entry.Expiration;
            Layered<T>().FillFromInnerRead(option, marks?[i] ?? 0, entry.Value, policy);
        }

        return results.ToArray();
    }

    private async ValueTask<T?> GetInnerAsync<T>(CacheEntryOptions options, CachePolicy policy)
    {
        if (_memoryCache.TryGetValue<ICacheEntry<T>>(options.CacheKey.Name, out var entry))
        {
            LogFoundLocal(Logged(options, typeof(T)));
            if(_connectionState.IsConnected)
            {
                MarkRead(entry);
                return entry!.Value;
            }
            else if (_useLocalOnlyWhenDisconnected)
            {
                LogUsingPrimaryOnlyWhenDisconnected(Logged(options, typeof(T)));
                MarkRead(entry);
                return entry!.Value;
            }
            else
            {
                LogReturningDefaultDisconnected(Logged(options, typeof(T)));
                RemoveLocal(options.CacheKey.Name);
                return default;
            }
        }

        var fetched = await Layered<T>().FetchInnerAsync(options, policy).ConfigureAwait(false);
        return fetched.Found ? fetched.Value : default;
    }

    private async ValueTask<ICacheEntry<T?>> GetCacheEntryMissAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token)
    {
        policy ??= _defaultPolicy;
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_multiLayerCacheOptions.DefaultExpiration), token);
        return await GetCacheEntryInnerAsync<T>(options, policy).ConfigureAwait(false);
    }

    private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesInnerAsync<T>(CacheEntryOptions[] options, CachePolicy policy, CancellationToken token = default)
    {
        var results = new KeyValuePair<CacheKey, ICacheEntry<T?>>[options.Length];
        List<int> missIndices = [];
        List<CacheEntryOptions> cacheEntriesToFetch = [];
        for (int i = 0; i < options.Length; i++)
        {
            var option = options[i];
            if (_memoryCache.TryGetValue<ICacheEntry<T?>>(option.CacheKey.Name, out var entry))
            {
                LogFoundLocal(Logged(option, typeof(T)));
                if (_connectionState.IsConnected || _useLocalOnlyWhenDisconnected)
                {
                    if (!_connectionState.IsConnected)
                    {
                        LogUsingPrimaryOnlyWhenDisconnected(Logged(option, typeof(T)));
                    }
                    MarkRead(entry);
                    results[i] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(option.CacheKey, entry!);
                    continue;
                }

                LogReturningDefaultDisconnected(Logged(option, typeof(T)));
                RemoveLocal(option.CacheKey.Name);
                results[i] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(option.CacheKey, _cacheEntryFactory.Create<T?>(default, DateTimeOffset.MinValue));
                continue;
            }

            missIndices.Add(i);
            cacheEntriesToFetch.Add(option);
        }

        if (cacheEntriesToFetch.Count == 0)
        {
            return results;
        }

        var keys = cacheEntriesToFetch.Select(c => c.CacheKey).ToArray();
        var marks = MarkInnerReads(keys, cacheEntriesToFetch[0].TopicKey);
        var fetched = await _innerCache.GetCacheEntriesAsync<T>(keys, policy, token).ConfigureAwait(false);

        for (int j = 0; j < keys.Length; j++)
        {
            var resultIndex = missIndices[j];
            var entry = fetched[j].Value;
            var key = fetched[j].Key;
            results[resultIndex] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(key, entry);

            if (!entry.Found)
            {
                continue;
            }

            LogFoundInnerCacheCopy(LoggedComposed(key, typeof(T)));
            var option = cacheEntriesToFetch[j];
            option.Expiration = entry.Expiration;
            Layered<T>().FillFromInnerRead(option, marks?[j] ?? 0, entry.Value, policy);
        }

        return results;
    }

    private async ValueTask<ICacheEntry<T?>> GetCacheEntryInnerAsync<T>(CacheEntryOptions options, CachePolicy policy)
    {
        if (_memoryCache.TryGetValue<ICacheEntry<T?>>(options.CacheKey.Name, out var entry))
        {
            LogFoundLocal(Logged(options, typeof(T)));
            if (_connectionState.IsConnected || _useLocalOnlyWhenDisconnected)
            {
                if (!_connectionState.IsConnected)
                {
                    LogUsingPrimaryOnlyWhenDisconnected(Logged(options, typeof(T)));
                }

                MarkRead(entry);
                return entry!;
            }

            LogReturningDefaultDisconnected(Logged(options, typeof(T)));
            RemoveLocal(options.CacheKey.Name);
            return _cacheEntryFactory.Create<T?>(default, DateTimeOffset.MinValue);
        }

        return await Layered<T>().FetchInnerAsync(options, policy).ConfigureAwait(false);
    }

    /// <summary>One topic for all: the keys hold one value type.</summary>
    private long[]? MarkInnerReads(CacheKey[] keys, TopicKey topic)
    {
        // Watched first: the first watch turns counting on, and its marks must be counted.
        var first = MarkInnerRead(keys[0].Name, topic);
        if (!_background.Counting)
        {
            return null;
        }

        var marks = new long[keys.Length];
        marks[0] = first;
        for (var i = 1; i < keys.Length; i++)
        {
            marks[i] = MarkInnerRead(keys[i].Name, topic);
        }

        return marks;
    }
}
