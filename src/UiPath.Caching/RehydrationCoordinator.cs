using System.Collections.Concurrent;
using System.Globalization;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal sealed class RehydrationCoordinator(
    string cacheName,
    CacheClock clock,
    IDistributedLock distributedLock,
    IDistributedLockKeyStrategy lockKeyStrategy,
    ICachingTelemetryProvider telemetry,
    ILogger logger)
{
    private const string EventTriggered = "cache.rehydrate.triggered";
    private const string EventSucceeded = "cache.rehydrate.succeeded";
    private const string EventFailed = "cache.rehydrate.failed";
    private const string EventDeduped = "cache.rehydrate.deduped";
    private const string EventTimedOut = "cache.rehydrate.timed_out";
    private const string TagCacheName = "cache.name";
    private const string TagCacheKey = "cache.key";
    private const string TagKind = "kind";
    private const string TagProfile = "profile";
    private const string TagReason = "reason";
    private const string TagExceptionType = "exception_type";
    private const string TagBatchSize = "batch.size";
    private const string ReasonNotAcquired = "not_acquired";
    private const string LockKeyPrefix = "rehydrate:";
    private const double MinTimeoutMs = 1000.0;
    private const int MaxBackoffShift = 30;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    // Timestamp lets us evict entries older than MaxCooldown so the dictionary can't grow
    // unbounded for high-cardinality caches where a key fails once and never recurs.
    private readonly ConcurrentDictionary<string, (int Count, long FailedAtTicks)> _failureCount = new(StringComparer.Ordinal);

    public bool TryTrigger(
        CacheKey cacheKey,
        DateTimeOffset entryExpiration,
        CachePolicy? policy,
        TimeSpan duration,
        string kind,
        Func<CancellationToken, ValueTask> rehydrateAsync) =>
        TryTriggerCore(
            [(cacheKey, entryExpiration)],
            policy,
            duration,
            kind,
            (_, ct) => rehydrateAsync(ct));

    public bool TryTriggerBatch(
        IReadOnlyList<(CacheKey Key, DateTimeOffset Expiration)> candidates,
        CachePolicy? policy,
        TimeSpan duration,
        string kind,
        Func<CacheKey[], CancellationToken, ValueTask> rehydrateAsync) =>
        TryTriggerCore(candidates, policy, duration, kind, rehydrateAsync);

    private bool TryTriggerCore(
        IReadOnlyList<(CacheKey Key, DateTimeOffset Expiration)> candidates,
        CachePolicy? policy,
        TimeSpan duration,
        string kind,
        Func<CacheKey[], CancellationToken, ValueTask> rehydrateAsync)
    {
        if (policy?.RehydrateEnabled != true || policy.Rehydrate is null)
        {
            return false;
        }
        if (duration <= TimeSpan.Zero)
        {
            return false;
        }

        var reserved = ReserveKeysPastThreshold(candidates, policy.Rehydrate.Threshold, duration);
        if (reserved.Count == 0)
        {
            return false;
        }

        var reservedKeys = reserved.ToArray();
        _ = SpawnAsync(reservedKeys, policy.Rehydrate, duration, kind, rehydrateAsync);
        return true;
    }

    /// <summary>Reserves the candidates past <paramref name="threshold"/> that this coordinator can claim.</summary>
    private List<CacheKey> ReserveKeysPastThreshold(
        IReadOnlyList<(CacheKey Key, DateTimeOffset Expiration)> candidates,
        double threshold,
        TimeSpan duration)
    {
        var reserved = new List<CacheKey>(candidates.Count);
        foreach (var (key, entryExpiration) in candidates)
        {
            var remaining = entryExpiration - clock.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                continue;
            }
            var elapsedFraction = (duration - remaining).TotalMilliseconds / duration.TotalMilliseconds;
            if (elapsedFraction < threshold)
            {
                continue;
            }
            if (!_inFlight.TryAdd(key.Name, 0))
            {
                continue;
            }
            reserved.Add(key);
        }
        return reserved;
    }

    private async Task SpawnAsync(
        CacheKey[] reservedKeys,
        RehydrateOptions options,
        TimeSpan duration,
        string kind,
        Func<CacheKey[], CancellationToken, ValueTask> rehydrateAsync)
    {
        var profile = options.Name ?? string.Empty;
        var groupKey = CompositeCacheKey.For(reservedKeys);
        var keys = Array.Empty<CacheKey>();
        List<IAsyncDisposable>? handles = null;
        try
        {
            var failureCount = reservedKeys.Max(k => ReadFailureCount(k.Name, options.MaxCooldown));
            var cooldown = ComputeCooldown(options.BaseCooldown, options.MaxCooldown, failureCount);
            var timeoutMs = Math.Min(int.MaxValue, Math.Max(MinTimeoutMs, options.TimeoutFraction * duration.TotalMilliseconds));
            var factoryTimeout = TimeSpan.FromMilliseconds(timeoutMs);
            // Failure paths leave handles=null so the locks hold for factoryTimeout+cooldown, which
            // gives BaseCooldown/MaxCooldown real retry-cadence control regardless of factory outcome.
            var lockExpiry = SafeAdd(factoryTimeout, cooldown);

            (keys, handles) = await AcquirePerKeyLocksAsync(reservedKeys, lockExpiry, factoryTimeout).ConfigureAwait(false);
            if (keys.Length == 0)
            {
                telemetry.TrackEvent(EventDeduped, Tags(KeyValuePair.Create(TagReason, ReasonNotAcquired)));
                return;
            }
            groupKey = CompositeCacheKey.For(keys);

            telemetry.TrackEvent(EventTriggered, Tags());

            using var cts = new CancellationTokenSource(factoryTimeout);
            try
            {
                await rehydrateAsync(keys, cts.Token).ConfigureAwait(false);
                ClearFailureCounts(keys);
                telemetry.TrackEvent(EventSucceeded, Tags());
                await ReleaseLocksAsync(handles, groupKey).ConfigureAwait(false);
                handles = null;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                IncrementFailureCounts(keys);
                telemetry.TrackEvent(EventTimedOut, Tags());
                handles = null;
            }
            catch (Exception ex)
            {
                IncrementFailureCounts(keys);
                telemetry.TrackEvent(EventFailed, Tags(KeyValuePair.Create(TagExceptionType, ex.GetType().Name)));
                handles = null;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rehydrate spawn failed for cache key {CacheKey}", groupKey.Name);
        }
        finally
        {
            ReleaseInFlight(reservedKeys);
            if (handles is not null)
            {
                await ReleaseLocksAsync(handles, groupKey).ConfigureAwait(false);
            }
        }

        KeyValuePair<string, string>[] Tags(params KeyValuePair<string, string>[] extra)
        {
            var size = keys.Length == 0 ? reservedKeys.Length : keys.Length;
            var tags = new List<KeyValuePair<string, string>>(5 + extra.Length)
            {
                new(TagCacheName, cacheName),
                new(TagCacheKey, groupKey.Name),
                new(TagKind, kind),
                new(TagProfile, profile),
            };
            if (size > 1)
            {
                tags.Add(new(TagBatchSize, size.ToString(CultureInfo.InvariantCulture)));
            }
            tags.AddRange(extra);
            return tags.ToArray();
        }
    }

    /// <summary>Takes one lock per reserved key and returns only the keys whose lock was won.</summary>
    private async Task<(CacheKey[] Keys, List<IAsyncDisposable> Handles)> AcquirePerKeyLocksAsync(
        CacheKey[] reservedKeys,
        TimeSpan lockExpiry,
        TimeSpan factoryTimeout)
    {
        var attempts = new Task<IAsyncDisposable?>[reservedKeys.Length];
        for (var i = 0; i < reservedKeys.Length; i++)
        {
            attempts[i] = TryAcquireOneAsync(reservedKeys[i], lockExpiry, factoryTimeout);
        }
        var results = await Task.WhenAll(attempts).ConfigureAwait(false);

        var keys = new List<CacheKey>(reservedKeys.Length);
        var handles = new List<IAsyncDisposable>(reservedKeys.Length);
        for (var i = 0; i < reservedKeys.Length; i++)
        {
            if (results[i] is { } handle)
            {
                keys.Add(reservedKeys[i]);
                handles.Add(handle);
            }
        }
        return (keys.ToArray(), handles);
    }

    private async Task<IAsyncDisposable?> TryAcquireOneAsync(CacheKey key, TimeSpan lockExpiry, TimeSpan factoryTimeout)
    {
        var lockKey = LockKeyPrefix + lockKeyStrategy.GetLockKey(key);
        try
        {
            return await FactoryTimeout.RunAsync<IAsyncDisposable?>(
                ct => distributedLock.TryAcquireAsync(lockKey, lockExpiry, ct).AsTask(),
                factoryTimeout,
                key,
                cacheName,
                telemetry,
                CancellationToken.None,
                source: FactoryTimeout.SourceRehydrateLock).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rehydrate lock acquire failed for cache key {CacheKey}", key.Name);
            return null;
        }
    }

    private async ValueTask ReleaseLocksAsync(List<IAsyncDisposable> handles, CacheKey groupKey)
    {
        foreach (var handle in handles)
        {
            await DisposeLockQuietlyAsync(handle, groupKey).ConfigureAwait(false);
        }
    }

    private void ClearFailureCounts(CacheKey[] keys)
    {
        foreach (var key in keys)
        {
            _failureCount.TryRemove(key.Name, out _);
        }
    }

    private void IncrementFailureCounts(CacheKey[] keys)
    {
        foreach (var key in keys)
        {
            IncrementFailureCount(key.Name);
        }
    }

    private void ReleaseInFlight(CacheKey[] keys)
    {
        foreach (var key in keys)
        {
            _inFlight.TryRemove(key.Name, out _);
        }
    }

    private async ValueTask DisposeLockQuietlyAsync(IAsyncDisposable handle, CacheKey groupKey)
    {
        try
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rehydrate cleanup dispose failed for cache key {CacheKey}", groupKey.Name);
        }
    }

    private static TimeSpan SafeAdd(TimeSpan a, TimeSpan b) =>
        a.Ticks > TimeSpan.MaxValue.Ticks - b.Ticks ? TimeSpan.MaxValue : a + b;

    private int ReadFailureCount(string key, TimeSpan maxCooldown)
    {
        if (!_failureCount.TryGetValue(key, out var entry))
        {
            return 0;
        }
        var nowTicks = clock.UtcNow.UtcTicks;
        if (nowTicks - entry.FailedAtTicks > maxCooldown.Ticks)
        {
            _failureCount.TryRemove(new KeyValuePair<string, (int, long)>(key, entry));
            return 0;
        }
        return entry.Count;
    }

    private void IncrementFailureCount(string key)
    {
        var nowTicks = clock.UtcNow.UtcTicks;
        _failureCount.AddOrUpdate(
            key,
            static (_, ts) => (1, ts),
            static (_, current, ts) => (current.Count + 1, ts),
            nowTicks);
    }

    private static TimeSpan ComputeCooldown(TimeSpan baseCooldown, TimeSpan maxCooldown, int failureCount)
    {
        if (failureCount <= 0)
        {
            return TimeSpan.FromTicks(Math.Min(baseCooldown.Ticks, maxCooldown.Ticks));
        }
        var shift = Math.Min(failureCount, MaxBackoffShift);
        var multiplier = 1L << shift;
        var ticks = baseCooldown.Ticks;
        if (ticks > 0 && multiplier > long.MaxValue / ticks)
        {
            return maxCooldown;
        }
        var product = ticks * multiplier;
        return TimeSpan.FromTicks(Math.Min(product, maxCooldown.Ticks));
    }
}
