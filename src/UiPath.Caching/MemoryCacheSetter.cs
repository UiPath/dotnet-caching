using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal abstract class MemoryCacheSetter(
    string cacheName,
    IChangeTokenFactory changeTokenFactory,
    ITopicProvider topicProvider,
    IMemoryCache memoryCache,
    ILogger logger,
    CacheClock clock,
    IMultilayerCacheOptions cacheOptions,
    IMemoryCacheOptions memoryCacheOptions,
    ICachingTelemetryProvider telemetryProvider
        )
{
    private const string EventRefreshMetadataFailed = "Caching." + nameof(MemoryCacheSetter) + "." + nameof(RefreshMetadata) + ".Failed";
    private const string PropCacheKey = "CacheKey";
    private const string PropTopicKey = "TopicKey";
    private const string PropTransportId = "TransportId";

    private ICacheEntrySizeProvider SizeProvider { get; } = memoryCacheOptions.SizeProvider ?? new DefaultCacheEntrySizeProvider();

    protected CacheClock Clock { get; } = clock;

    public bool Set(ICacheEntryOptions options, ICacheEntry item, Type entryType, TimeSpan? maxExpiration)
    {
        try
        {
            var topic = topicProvider.Create(options.TopicKey);
            var token = changeTokenFactory.Create(options.CacheKey, topic, cacheName, entryType);
            var state = new RefreshMetadataState(options.CacheKey, options.TopicKey, item, token, entryType, maxExpiration);
            token.RegisterChangeCallback(RefreshMetadata, state);
            var memOptions = new MemoryCacheEntryOptions();
            var expiration = GetCacheExpiration(options.Expiration, maxExpiration);
            memOptions.SetAbsoluteExpiration(expiration);
            memOptions.ExpirationTokens.Add(token);
            memOptions.RegisterPostEvictionCallback(PostEviction, token);
            if(memoryCacheOptions.SizeLimit.HasValue)
            {
                memOptions.SetSize(SizeProvider.GetSize(item));
            }
            memoryCache.Set(options.CacheKey, item, memOptions);
            return true;
        }
        catch (Exception ex)
        {
            memoryCache.Remove(options.CacheKey);
            logger.LogWarning(ex, "Unable to set local memory for {CacheKey}", options.CacheKey);
            return false;
        }
    }

    static void PostEviction(object key, object? value, EvictionReason reason, object? state)
    {
        if (state is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    internal void RefreshMetadata(object? state)
    {
        if (state is RefreshMetadataState metadataState)
        {
            RefreshMetadata(metadataState);
        }
    }

    private void RefreshMetadata(RefreshMetadataState metadataState)
    {
        var token = metadataState.Token;
        if (!token.MetadataHasChanged && token.Expiration == null)
        {
            return;
        }

        bool set = default;

        try
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(cacheOptions.Timeout);
            var options = CreateEntry(metadataState, cts.Token);
            var newEntry = metadataState.CacheEntity.NewEntry(options.Expiration, options.Metadata);
            set = Set(options, newEntry, metadataState.EntryType, metadataState.MaxExpiration);

        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to refresh cache cacheKey {CacheKey}", metadataState.CacheKey);
        }
        finally
        {
            if (!set)
            {
                telemetryProvider.TrackEvent(EventRefreshMetadataFailed,
                [
                    new(PropCacheKey, metadataState.CacheKey),
                    new(PropTopicKey, metadataState.TopicKey),
                    new(PropTransportId, token.TransportId ?? string.Empty),
                ]);
            }
        }

    }

    protected abstract ICacheEntryOptions CreateEntry(RefreshMetadataState metadataState, CancellationToken cancellationToken);

    private DateTimeOffset GetCacheExpiration(DateTimeOffset expiration, TimeSpan? maxExpiration)
    {
        if (maxExpiration.HasValue)
        {
            var maxExp = Clock.UtcNow.Add(maxExpiration.Value);
            if (expiration > maxExp)
            {
                return maxExp;
            }
        }

        return expiration;
    }
}
