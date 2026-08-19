using Microsoft.Extensions.Caching.Distributed;

namespace UiPath.Caching.Distributed;

/// <summary><see cref="IDistributedCache"/> over an <see cref="ICache"/>: always-sensitive keys, raw-byte envelope payloads.</summary>
internal sealed class UiPathDistributedCache : IDistributedCache
{
    private readonly ICache _cache;
    private readonly CachePolicy? _policy;
    private readonly string _instanceName;
    private readonly bool _slideByRewrite;
    private readonly ISystemClock _clock;
    private readonly ILogger<UiPathDistributedCache> _logger;

    public UiPathDistributedCache(
        ICache cache,
        UiPathDistributedCacheOptions options,
        ICachePolicyFactory? policyFactory,
        ILogger<UiPathDistributedCache> logger,
        ISystemClock? clock = null,
        bool slideByRewrite = false)
    {
        _cache = cache;
        _instanceName = options.InstanceName ?? string.Empty;
        _policy = options.PolicyName is { } policyName ? policyFactory?.Resolve(policyName) : null;
        _slideByRewrite = slideByRewrite;
        _logger = logger;
        _clock = clock ?? new SystemClock();
    }

    public byte[]? Get(string key) =>
        GetAsync(key).GetAwaiter().GetResult();

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        var envelope = await GetEnvelopeAndSlideAsync(key, token).ConfigureAwait(false);
        return envelope?.Data;
    }

    public void Refresh(string key) =>
        RefreshAsync(key).GetAwaiter().GetResult();

    public async Task RefreshAsync(string key, CancellationToken token = default) =>
        _ = await GetEnvelopeAndSlideAsync(key, token).ConfigureAwait(false);

    public void Remove(string key) =>
        RemoveAsync(key).GetAwaiter().GetResult();

    public Task RemoveAsync(string key, CancellationToken token = default) =>
        _cache.RemoveAsync<byte[]>(Encode(key), token).AsTask();

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        SetAsync(key, value, options).GetAwaiter().GetResult();

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        var now = _clock.UtcNow;
        var absolute = ResolveAbsoluteExpiration(now, options);
        var envelope = new DistributedCacheEnvelope(value, options.SlidingExpiration?.Ticks, absolute);

        TimeSpan? ttl = (options.SlidingExpiration, absolute) switch
        {
            ({ } sliding, { } cap) => TimeSpan.FromTicks(Math.Min(sliding.Ticks, (cap - now).Ticks)),
            ({ } sliding, null) => sliding,
            (null, { } cap) => cap - now,
            _ => null,
        };

        _ = await _cache.SetAsync(Encode(key), envelope.Encode(), ttl, _policy, token).ConfigureAwait(false);
    }

    private CacheKey Encode(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new CacheKey(_instanceName + key, CacheKeyCasing.Sensitive);
    }

    private async ValueTask<DistributedCacheEnvelope?> GetEnvelopeAndSlideAsync(string key, CancellationToken token)
    {
        var cacheKey = Encode(key);
        var stored = await _cache.GetAsync<byte[]>(cacheKey, _policy, token).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        var envelope = DistributedCacheEnvelope.TryDecode(stored);
        if (envelope is null)
        {
            _logger.LogWarning("Value for distributed cache key {Key} has no envelope header; treating as a miss.", key);
            return null;
        }

        if (envelope.SlidingTicks is { } slidingTicks)
        {
            var now = _clock.UtcNow;
            var target = now.AddTicks(slidingTicks);
            if (envelope.AbsoluteExpiration is { } absolute && absolute < target)
            {
                target = absolute;
            }

            if (_slideByRewrite)
            {
                _ = await _cache.SetAsync(cacheKey, stored, target - now, _policy, token).ConfigureAwait(false);
            }
            else
            {
                _ = await _cache.RefreshAsync<byte[]>(cacheKey, (DateTimeOffset?)target, _policy, token).ConfigureAwait(false);
            }
        }

        return envelope;
    }

    private static DateTimeOffset? ResolveAbsoluteExpiration(DateTimeOffset now, DistributedCacheEntryOptions options)
    {
        if (options.AbsoluteExpiration is { } absolute)
        {
            return absolute <= now
                ? throw new ArgumentOutOfRangeException(nameof(options), absolute, "The absolute expiration must be in the future.")
                : absolute;
        }

        return options.AbsoluteExpirationRelativeToNow is { } relative ? now.Add(relative) : null;
    }
}
