using System.Diagnostics;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

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
                var contendedStr = contended.ToString();
                _telemetry.TrackException(ex, [new(PropOperation, OperationAcquire), new(PropKey, key), new(PropContended, contendedStr)]);
                _telemetry.TrackEvent(EventUnavailable, [new(PropKey, key), new(PropContended, contendedStr)]);
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
                return hasDeadline ? TrackTimeoutNoOp(key) : NoOpAsyncDisposable.Instance;
            }

            await Task.Delay(delay, token).ConfigureAwait(false);
            pollInterval = NextPollInterval(pollInterval, _maxPollInterval);
        }
    }

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan expiry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var redisKey = new RedisKey(key);
        var lockToken = BuildLockToken();

        bool acquired;
        try
        {
            acquired = await _redis.Database.LockTakeAsync(redisKey, lockToken, expiry, CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _telemetry.TrackException(ex, [new(PropOperation, OperationAcquire), new(PropKey, key), new(PropContended, bool.FalseString)]);
            _telemetry.TrackEvent(EventUnavailable, [new(PropKey, key), new(PropContended, bool.FalseString)]);
            return null;
        }

        return acquired ? BuildAcquiredLease(redisKey, lockToken, key, contended: false) : null;
    }

    private string BuildLockToken() =>
        string.Create(_tokenPrefix.Length + 32, _tokenPrefix, static (span, prefix) =>
        {
            prefix.AsSpan().CopyTo(span);
            Guid.NewGuid().TryFormat(span[prefix.Length..], out _, "N");
        });

    internal static TimeSpan NextPollInterval(TimeSpan current, TimeSpan max) =>
        current < max
            ? TimeSpan.FromTicks(Math.Min(current.Ticks * 2, max.Ticks))
            : max;

    private static TimeSpan ComputeRetryDelay(bool hasDeadline, long startTimestamp, TimeSpan waitTimeout, TimeSpan pollInterval) =>
        ComputeRetryDelayWithJitter(hasDeadline, startTimestamp, waitTimeout, pollInterval, Random.Shared.NextDouble());

    internal static TimeSpan ComputeRetryDelayWithJitter(bool hasDeadline, long startTimestamp, TimeSpan waitTimeout, TimeSpan pollInterval, double jitterUnit)
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
        var jitterFactor = 0.8 + jitterUnit * 0.4;
        var jittered = TimeSpan.FromTicks((long)(pollInterval.Ticks * jitterFactor));
        return jittered < remaining ? jittered : remaining;
    }

    private Releaser BuildAcquiredLease(RedisKey redisKey, RedisValue lockToken, string key, bool contended)
    {
        _telemetry.TrackEvent(EventAcquired, [new(PropKey, key), new(PropContended, contended.ToString())]);
        return new Releaser(_redis, _telemetry, redisKey, lockToken);
    }

    private NoOpAsyncDisposable TrackTimeoutNoOp(string key)
    {
        _telemetry.TrackEvent(EventTimeout, [new(PropKey, key), new(PropContended, bool.TrueString)]);
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
                telemetry.TrackException(ex, [new(PropOperation, OperationRelease), new(PropKey, redisKey.ToString())]);
            }
        }
    }
}
