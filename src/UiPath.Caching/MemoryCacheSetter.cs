using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal abstract class MemoryCacheSetter(
    string cacheName,
    IChangeTokenFactory changeTokenFactory,
    ITopicProvider topicProvider,
    IMemoryCache memoryCache,
    ILogger logger,
    TimeProvider clock,
    IMultilayerCacheOptions cacheOptions,
    IMemoryCacheOptions memoryCacheOptions,
    ICachingTelemetryProvider telemetryProvider,
    KeyMasker? masker = null
        )
{

    private const string EventRefreshMetadataFailed = "Caching." + nameof(MemoryCacheSetter) + "." + nameof(RefreshMetadata) + ".Failed";
    private const string PropCacheKey = "CacheKey";
    private const string PropTopicKey = "TopicKey";
    private const string PropTransportId = "TransportId";
    private readonly KeyMasker _masker = masker ?? KeyMasker.Off;

    protected TimeProvider Clock { get; } = clock;

    private ICacheEntrySizeProvider SizeProvider { get; } = memoryCacheOptions.SizeProvider ?? new DefaultCacheEntrySizeProvider();

    public bool Set(ICacheEntryOptions options, ICacheEntry item, Type entryType, TimeSpan? maxExpiration)
    {
        try
        {
            var topic = topicProvider.Create(options.TopicKey);
            var token = changeTokenFactory is IMaskedChangeTokenFactory masked
                ? masked.Create(options.CacheKey, topic, cacheName, entryType, _masker, options.CallerKey)
                : changeTokenFactory.Create(options.CacheKey, topic, cacheName, entryType);
            var state = new RefreshMetadataState(options.CacheKey, options.TopicKey, item, token, entryType, maxExpiration, options.CallerKey);
            token.RegisterChangeCallback(RefreshMetadata, state);
            // Filled in place rather than through MemoryCacheEntryOptions, which is copied into the entry and discarded.
            // The entry is committed when disposed, and only once its value is set, so a throw before that stores nothing.
            using var entry = memoryCache.CreateEntry(options.CacheKey.Name);
            entry.AbsoluteExpiration = GetCacheExpiration(options.Expiration, maxExpiration);
            entry.ExpirationTokens.Add(token);
            entry.RegisterPostEvictionCallback(PostEviction, token);
            if (memoryCacheOptions.SizeLimit.HasValue)
            {
                entry.Size = SizeProvider.GetSize(item);
            }
            entry.Value = item;
            return true;
        }
        catch (Exception ex)
        {
            memoryCache.Remove(options.CacheKey.Name);
            logger.LogWarning(ex, "Unable to set local memory for {CacheKey}", LoggedKey.For(_masker, options.CallerKey, options.CacheKey.Name, entryType));
            return false;
        }
    }

    internal void RefreshMetadata(object? state)
    {
        if (state is RefreshMetadataState metadataState)
        {
            RefreshMetadata(metadataState);
        }
    }

    protected abstract ICacheEntryOptions CreateEntry(RefreshMetadataState metadataState, CancellationToken cancellationToken);

    private static void PostEviction(object key, object? value, EvictionReason reason, object? state)
    {
        if (state is IDisposable disposable)
        {
            disposable.Dispose();
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
            logger.LogWarning(ex, "Unable to refresh cache cacheKey {CacheKey}", LoggedKey.For(_masker, metadataState.CallerKey, metadataState.CacheKey.Name, metadataState.EntryType));
        }
        finally
        {
            if (!set)
            {
                telemetryProvider.TryTrackEvent(EventRefreshMetadataFailed,
                [
                    new(PropCacheKey, metadataState.CacheKey),
                    new(PropTopicKey, metadataState.TopicKey),
                    new(PropTransportId, token.TransportId ?? string.Empty),
                ]);
            }
        }

    }

    private DateTimeOffset GetCacheExpiration(DateTimeOffset expiration, TimeSpan? maxExpiration)
    {
        if (maxExpiration.HasValue)
        {
            var maxExp = Clock.ToDateTimeOffset(maxExpiration.Value);
            if (expiration > maxExp)
            {
                return maxExp;
            }
        }

        return expiration;
    }
}
