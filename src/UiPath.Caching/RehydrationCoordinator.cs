using System.Collections.Concurrent;
using System.Globalization;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal sealed class RehydrationCoordinator(
    string cacheName,
    TimeProvider clock,
    IDistributedLock distributedLock,
    IDistributedLockKeyStrategy lockKeyStrategy,
    ICachingTelemetryProvider telemetry,
    ILogger logger,
    KeyMasker? masker = null)
{

    private const string EventTriggered = "cache.rehydrate.triggered";
    private const string EventSucceeded = "cache.rehydrate.succeeded";
    private const string EventAdopted = "cache.rehydrate.adopted";
    private const string EventAdoptReadFailed = "cache.rehydrate.adopt_read_failed";
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
    private const string TagAdopted = "adopted";
    private const string ReasonNotAcquired = "not_acquired";
    private const string LockKeyPrefix = "rehydrate:";
    private const double MinTimeoutMs = 1000.0;
    // The read of an adoptable inner copy runs on its own budget, added to the run's timeout and lock, so an unhealthy inner tier cannot spend the generator's.
    private const double AdoptReadFractionOfTimeout = 0.25;
    private const int MaxBackoffShift = 30;
    private static readonly TimeSpan MaxAdoptReadTimeout = TimeSpan.FromSeconds(2);
    private readonly KeyMasker _masker = masker ?? KeyMasker.Off;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    // Timestamp lets us evict entries older than MaxCooldown so the dictionary can't grow
    // unbounded for high-cardinality caches where a key fails once and never recurs.
    private readonly ConcurrentDictionary<string, (int Count, long FailedAtTicks)> _failureCount = new(StringComparer.Ordinal);

    /// <param name="rehydrateAsync">Records on the run the keys it adopted from the inner tier instead of generating.</param>
    public bool TryTrigger(
        CacheKey cacheKey,
        DateTimeOffset entryExpiration,
        CachePolicy? policy,
        TimeSpan duration,
        string kind,
        Func<RehydrateRun, CancellationToken, ValueTask> rehydrateAsync,
        Type? entryType = null) =>
        TryTriggerCore(
            [(cacheKey, entryExpiration)],
            policy,
            duration,
            kind,
            (_, run, ct) => rehydrateAsync(run, ct),
            entryType);

    public bool TryTriggerBatch(
        IReadOnlyList<(CacheKey Key, DateTimeOffset Expiration)> candidates,
        CachePolicy? policy,
        TimeSpan duration,
        string kind,
        Func<CacheKey[], RehydrateRun, CancellationToken, ValueTask> rehydrateAsync,
        Type? entryType = null) =>
        TryTriggerCore(candidates, policy, duration, kind, rehydrateAsync, entryType);

    internal static TimeSpan RunTimeout(RehydrateOptions options, TimeSpan duration) =>
        TimeSpan.FromMilliseconds(Math.Min(int.MaxValue, Math.Max(MinTimeoutMs, options.TimeoutFraction * duration.TotalMilliseconds)));

    internal static TimeSpan AdoptReadTimeout(RehydrateOptions options, TimeSpan duration)
    {
        var budget = TimeSpan.FromTicks((long)(RunTimeout(options, duration).Ticks * AdoptReadFractionOfTimeout));
        return budget > MaxAdoptReadTimeout ? MaxAdoptReadTimeout : budget;
    }

    internal void TrackAdoptReadFailed(RehydrateOptions options, string kind, CacheKey key, string reason) =>
        telemetry.TryTrackEvent(
            EventAdoptReadFailed,
            [
                new(TagCacheName, cacheName),
                new(TagCacheKey, key.Name),
                new(TagKind, kind),
                new(TagProfile, options.Name ?? string.Empty),
                new(TagReason, reason),
            ]);

    private static KeyValuePair<string, string>[] AdoptedTag(RehydrateRun run) =>
        run.Adopted.Count == 0 ? [] : [KeyValuePair.Create(TagAdopted, run.Adopted.Count.ToString(CultureInfo.InvariantCulture))];

    private static TimeSpan SafeAdd(TimeSpan a, TimeSpan b) =>
        a.Ticks > TimeSpan.MaxValue.Ticks - b.Ticks ? TimeSpan.MaxValue : a + b;

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

    private bool TryTriggerCore(
        IReadOnlyList<(CacheKey Key, DateTimeOffset Expiration)> candidates,
        CachePolicy? policy,
        TimeSpan duration,
        string kind,
        Func<CacheKey[], RehydrateRun, CancellationToken, ValueTask> rehydrateAsync,
        Type? entryType)
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
        SpawnAsync(reservedKeys, policy.Rehydrate, duration, kind, rehydrateAsync, entryType).Forget();
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
            var remaining = entryExpiration - clock.GetUtcNow();
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
        Func<CacheKey[], RehydrateRun, CancellationToken, ValueTask> rehydrateAsync,
        Type? entryType)
    {
        var profile = options.Name ?? string.Empty;
        var groupKey = CompositeCacheKey.For(reservedKeys);
        var keys = Array.Empty<CacheKey>();
        List<IAsyncDisposable>? handles = null;
        try
        {
            var factoryTimeout = RunTimeout(options, duration);
            var runTimeout = SafeAdd(factoryTimeout, AdoptReadTimeout(options, duration));
            // Failure paths leave handles=null so each lock holds for the run timeout plus its own key's cooldown, which
            // gives BaseCooldown/MaxCooldown real retry-cadence control regardless of factory outcome.
            var lockExpiries = Array.ConvertAll(
                reservedKeys,
                key => SafeAdd(runTimeout, ComputeCooldown(options.BaseCooldown, options.MaxCooldown, ReadFailureCount(key.Name, options.MaxCooldown))));

            (keys, handles) = await AcquirePerKeyLocksAsync(reservedKeys, lockExpiries, factoryTimeout, entryType).ConfigureAwait(false);
            if (keys.Length == 0)
            {
                telemetry.TryTrackEvent(EventDeduped, Tags(KeyValuePair.Create(TagReason, ReasonNotAcquired)));
                return;
            }
            groupKey = CompositeCacheKey.For(keys);

            telemetry.TryTrackEvent(EventTriggered, Tags());

            using var cts = new CancellationTokenSource(runTimeout, clock);
            var run = new RehydrateRun();
            try
            {
                await rehydrateAsync(keys, run, cts.Token).ConfigureAwait(false);
                ClearFailureCounts(keys);
                if (run.Adopted.Count >= keys.Length)
                {
                    telemetry.TryTrackEvent(EventAdopted, Tags());
                }
                else
                {
                    telemetry.TryTrackEvent(EventSucceeded, Tags(AdoptedTag(run)));
                }

                await ReleaseLocksAsync(handles, groupKey, entryType).ConfigureAwait(false);
                handles = null;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                await SettleFailedRunAsync(keys, handles!, run, groupKey, entryType).ConfigureAwait(false);
                telemetry.TryTrackEvent(EventTimedOut, Tags(AdoptedTag(run)));
                handles = null;
            }
            catch (Exception ex)
            {
                await SettleFailedRunAsync(keys, handles!, run, groupKey, entryType).ConfigureAwait(false);
                telemetry.TryTrackEvent(EventFailed, Tags(AdoptedTag(run).Append(KeyValuePair.Create(TagExceptionType, ex.GetType().Name)).ToArray()));
                handles = null;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rehydrate spawn failed for cache key {CacheKey}", LoggedKey.For(_masker, groupKey, entryType));
        }
        finally
        {
            ReleaseInFlight(reservedKeys);
            if (handles is not null)
            {
                await ReleaseLocksAsync(handles, groupKey, entryType).ConfigureAwait(false);
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

    /// <summary>Only the keys that were generated count as failed and keep their cooldown lock; the adopted ones succeeded.</summary>
    private async Task SettleFailedRunAsync(CacheKey[] keys, List<IAsyncDisposable> handles, RehydrateRun run, CacheKey groupKey, Type? entryType)
    {
        var adopted = new HashSet<string>(run.Adopted.Select(k => k.Name), StringComparer.Ordinal);
        var generated = new List<CacheKey>(keys.Length);
        var released = new List<IAsyncDisposable>(adopted.Count);
        for (var i = 0; i < keys.Length; i++)
        {
            if (adopted.Contains(keys[i].Name))
            {
                released.Add(handles[i]);
                _failureCount.TryRemove(keys[i].Name, out _);
            }
            else
            {
                generated.Add(keys[i]);
            }
        }

        IncrementFailureCounts(generated.ToArray());
        await ReleaseLocksAsync(released, groupKey, entryType).ConfigureAwait(false);
    }

    /// <summary>Takes one lock per reserved key and returns only the keys whose lock was won.</summary>
    private async Task<(CacheKey[] Keys, List<IAsyncDisposable> Handles)> AcquirePerKeyLocksAsync(
        CacheKey[] reservedKeys,
        TimeSpan[] lockExpiries,
        TimeSpan factoryTimeout,
        Type? entryType)
    {
        var attempts = new Task<IAsyncDisposable?>[reservedKeys.Length];
        for (var i = 0; i < reservedKeys.Length; i++)
        {
            attempts[i] = TryAcquireOneAsync(reservedKeys[i], lockExpiries[i], factoryTimeout, entryType);
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

    private async Task<IAsyncDisposable?> TryAcquireOneAsync(CacheKey key, TimeSpan lockExpiry, TimeSpan factoryTimeout, Type? entryType)
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
            logger.LogError(ex, "Rehydrate lock acquire failed for cache key {CacheKey}", LoggedKey.For(_masker, key, entryType));
            return null;
        }
    }

    private async ValueTask ReleaseLocksAsync(List<IAsyncDisposable> handles, CacheKey groupKey, Type? entryType)
    {
        foreach (var handle in handles)
        {
            await DisposeLockQuietlyAsync(handle, groupKey, entryType).ConfigureAwait(false);
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

    private async ValueTask DisposeLockQuietlyAsync(IAsyncDisposable handle, CacheKey groupKey, Type? entryType)
    {
        try
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rehydrate cleanup dispose failed for cache key {CacheKey}", LoggedKey.For(_masker, groupKey, entryType));
        }
    }

    private int ReadFailureCount(string key, TimeSpan maxCooldown)
    {
        if (!_failureCount.TryGetValue(key, out var entry))
        {
            return 0;
        }
        var nowTicks = clock.GetUtcNow().UtcTicks;
        if (nowTicks - entry.FailedAtTicks > maxCooldown.Ticks)
        {
            _failureCount.TryRemove(new KeyValuePair<string, (int, long)>(key, entry));
            return 0;
        }
        return entry.Count;
    }

    private void IncrementFailureCount(string key)
    {
        var nowTicks = clock.GetUtcNow().UtcTicks;
        _failureCount.AddOrUpdate(
            key,
            static (_, ts) => (1, ts),
            static (_, current, ts) => (current.Count + 1, ts),
            nowTicks);
    }
}
