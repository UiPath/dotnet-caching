using System.Diagnostics;
using UiPath.Platform.Caching.Locking;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

internal sealed class RedisDistributedLock : IDistributedLock
{
    private const string EventAcquired = "cache.distributedlock.acquired";
    private const string EventTimeout = "cache.distributedlock.timeout";
    private const string EventUnavailable = "cache.distributedlock.unavailable";
    private const string PropOperation = "operation";
    private const string PropKey = "key";
    private const string PropContended = "contended";
    private const string OperationAcquire = "distributedlock.acquire";
    private const string OperationRelease = "distributedlock.release";
    private const string PollIntervalMustBePositive = "Must be greater than zero.";
    private const string MaxPollIntervalMustBeAtLeastPollInterval = "Must be greater than or equal to " + nameof(CacheOptions.DistributedLockPollInterval) + ".";

    private readonly IRedisConnector _redis;
    private readonly ICachingTelemetryProvider _telemetry;
    private readonly string _tokenPrefix;
    private readonly TimeSpan _initialPollInterval;
    private readonly TimeSpan _maxPollInterval;

    public RedisDistributedLock(
        IRedisConnector redis,
        IOptions<CacheOptions> cacheOptions,
        ICachingTelemetryProvider telemetry)
    {
        _redis = redis;
        _telemetry = telemetry;
        var opts = cacheOptions.Value;
        if (opts.DistributedLockPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                $"{nameof(cacheOptions)}.{nameof(CacheOptions.DistributedLockPollInterval)}",
                opts.DistributedLockPollInterval,
                PollIntervalMustBePositive);
        }
        if (opts.DistributedLockMaxPollInterval < opts.DistributedLockPollInterval)
        {
            throw new ArgumentOutOfRangeException(
                $"{nameof(cacheOptions)}.{nameof(CacheOptions.DistributedLockMaxPollInterval)}",
                opts.DistributedLockMaxPollInterval,
                MaxPollIntervalMustBeAtLeastPollInterval);
        }
        var sourceUri = opts.SourceUri ?? CacheOptions.MachineUri;
        _tokenPrefix = sourceUri.ToString() + ":";
        _initialPollInterval = opts.DistributedLockPollInterval;
        _maxPollInterval = opts.DistributedLockMaxPollInterval;
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(string key, TimeSpan expiry, TimeSpan waitTimeout, CancellationToken token)
    {
        var redisKey = new RedisKey(key);
        var lockToken = BuildLockToken();
        var hasDeadline = waitTimeout > TimeSpan.Zero;
        var startTimestamp = hasDeadline ? Stopwatch.GetTimestamp() : 0L;
        var contended = false;
        var pollInterval = _initialPollInterval;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            bool acquired;
            try
            {
                acquired = await _redis.Database.LockTakeAsync(redisKey, lockToken, expiry, CommandFlags.DemandMaster).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _telemetry.TrackException(ex, new Dictionary<string, string> { [PropOperation] = OperationAcquire, [PropKey] = key, [PropContended] = contended.ToString() });
                _telemetry.TrackEvent(EventUnavailable, new Dictionary<string, string> { [PropKey] = key, [PropContended] = contended.ToString() });
                return NoOpAsyncDisposable.Instance;
            }

            if (acquired)
            {
                return BuildAcquiredLease(redisKey, lockToken, key, contended);
            }

            contended = true;
            var delay = ComputeRetryDelay(hasDeadline, startTimestamp, waitTimeout, pollInterval);
            if (delay <= TimeSpan.Zero)
            {
                return TrackTimeoutNoOp(key);
            }

            await Task.Delay(delay, token).ConfigureAwait(false);
            pollInterval = pollInterval < _maxPollInterval ? TimeSpan.FromTicks(Math.Min(pollInterval.Ticks * 2, _maxPollInterval.Ticks)) : _maxPollInterval;
        }
    }

    private string BuildLockToken() =>
        string.Create(_tokenPrefix.Length + 32, _tokenPrefix, static (span, prefix) =>
        {
            prefix.AsSpan().CopyTo(span);
            Guid.NewGuid().TryFormat(span[prefix.Length..], out _, "N");
        });

    private static TimeSpan ComputeRetryDelay(bool hasDeadline, long startTimestamp, TimeSpan waitTimeout, TimeSpan pollInterval)
    {
        if (!hasDeadline)
        {
            return TimeSpan.Zero;
        }
        var remaining = waitTimeout - Stopwatch.GetElapsedTime(startTimestamp);
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        var jitterFactor = 0.8 + Random.Shared.NextDouble() * 0.4;
        var jittered = TimeSpan.FromTicks((long)(pollInterval.Ticks * jitterFactor));
        return jittered < remaining ? jittered : remaining;
    }

    private Releaser BuildAcquiredLease(RedisKey redisKey, RedisValue lockToken, string key, bool contended)
    {
        _telemetry.TrackEvent(EventAcquired, new Dictionary<string, string> { [PropKey] = key, [PropContended] = contended.ToString() });
        return new Releaser(_redis, _telemetry, redisKey, lockToken);
    }

    private NoOpAsyncDisposable TrackTimeoutNoOp(string key)
    {
        _telemetry.TrackEvent(EventTimeout, new Dictionary<string, string> { [PropKey] = key, [PropContended] = bool.TrueString });
        return NoOpAsyncDisposable.Instance;
    }

    private sealed class Releaser(IRedisConnector redis, ICachingTelemetryProvider telemetry, RedisKey redisKey, RedisValue lockToken) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await redis.Database.LockReleaseAsync(redisKey, lockToken, CommandFlags.DemandMaster).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    [PropOperation] = OperationRelease,
                    [PropKey] = redisKey.ToString(),
                });
            }
        }
    }
}
