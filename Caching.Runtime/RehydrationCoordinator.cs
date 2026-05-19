using System.Collections.Concurrent;
using UiPath.Platform.Caching.Locking;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;

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
        Func<CancellationToken, ValueTask> rehydrateAsync)
    {
        if (policy?.RehydrateEnabled != true || policy.Rehydrate is null)
        {
            return false;
        }
        if (duration <= TimeSpan.Zero)
        {
            return false;
        }
        var remaining = entryExpiration - clock.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }
        var elapsedFraction = (duration - remaining).TotalMilliseconds / duration.TotalMilliseconds;
        if (elapsedFraction < policy.Rehydrate.Threshold)
        {
            return false;
        }

        if (!_inFlight.TryAdd(cacheKey.Name, 0))
        {
            return false;
        }

        _ = SpawnAsync(cacheKey, policy.Rehydrate, duration, kind, rehydrateAsync);
        return true;
    }

    private async Task SpawnAsync(
        CacheKey cacheKey,
        RehydrateOptions options,
        TimeSpan duration,
        string kind,
        Func<CancellationToken, ValueTask> rehydrateAsync)
    {
        var profile = options.Name ?? string.Empty;
        IAsyncDisposable? handle = null;
        try
        {
            var failureCount = ReadFailureCount(cacheKey.Name, options.MaxCooldown);
            var cooldown = ComputeCooldown(options.BaseCooldown, options.MaxCooldown, failureCount);
            var timeoutMs = Math.Min(int.MaxValue, Math.Max(MinTimeoutMs, options.TimeoutFraction * duration.TotalMilliseconds));
            var factoryTimeout = TimeSpan.FromMilliseconds(timeoutMs);
            // Failure paths leave handle=null so the lock holds for factoryTimeout+cooldown, which
            // gives BaseCooldown/MaxCooldown real retry-cadence control regardless of factory outcome.
            var lockExpiry = SafeAdd(factoryTimeout, cooldown);
            var lockKey = LockKeyPrefix + lockKeyStrategy.GetLockKey(cacheKey);
            try
            {
                handle = await FactoryTimeout.RunAsync<IAsyncDisposable?>(
                    ct => distributedLock.TryAcquireAsync(lockKey, lockExpiry, ct).AsTask(),
                    factoryTimeout,
                    cacheKey,
                    cacheName,
                    telemetry,
                    CancellationToken.None,
                    source: FactoryTimeout.SourceRehydrateLock).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                handle = null;
            }
            if (handle is null)
            {
                telemetry.TrackEvent(EventDeduped,
                [
                    new(TagCacheName, cacheName),
                    new(TagCacheKey, cacheKey.Name),
                    new(TagKind, kind),
                    new(TagProfile, profile),
                    new(TagReason, ReasonNotAcquired),
                ]);
                return;
            }

            telemetry.TrackEvent(EventTriggered,
            [
                new(TagCacheName, cacheName),
                new(TagCacheKey, cacheKey.Name),
                new(TagKind, kind),
                new(TagProfile, profile),
            ]);

            using var cts = new CancellationTokenSource(factoryTimeout);
            try
            {
                await rehydrateAsync(cts.Token).ConfigureAwait(false);
                _failureCount.TryRemove(cacheKey.Name, out _);
                telemetry.TrackEvent(EventSucceeded,
                [
                    new(TagCacheName, cacheName),
                    new(TagCacheKey, cacheKey.Name),
                    new(TagKind, kind),
                    new(TagProfile, profile),
                ]);
                await handle.DisposeAsync().ConfigureAwait(false);
                handle = null;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                IncrementFailureCount(cacheKey.Name);
                telemetry.TrackEvent(EventTimedOut,
                [
                    new(TagCacheName, cacheName),
                    new(TagCacheKey, cacheKey.Name),
                    new(TagKind, kind),
                    new(TagProfile, profile),
                ]);
                handle = null;
            }
            catch (Exception ex)
            {
                IncrementFailureCount(cacheKey.Name);
                telemetry.TrackEvent(EventFailed,
                [
                    new(TagCacheName, cacheName),
                    new(TagCacheKey, cacheKey.Name),
                    new(TagKind, kind),
                    new(TagProfile, profile),
                    new(TagExceptionType, ex.GetType().Name),
                ]);
                handle = null;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rehydrate spawn failed for cache key {CacheKey}", cacheKey.Name);
        }
        finally
        {
            _inFlight.TryRemove(cacheKey.Name, out _);
            if (handle is not null)
            {
                try
                {
                    await handle.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Rehydrate cleanup dispose failed for cache key {CacheKey}", cacheKey.Name);
                }
            }
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
