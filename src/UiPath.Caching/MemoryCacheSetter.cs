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
    KeyMasker? masker = null,
    bool ignoreSubscriptionGaps = false
        )
{

    private const string EventRefreshMetadataFailed = "Caching." + nameof(MemoryCacheSetter) + "." + nameof(RefreshMetadata) + ".Failed";
    private const string PropCacheKey = "CacheKey";
    private const string PropTopicKey = "TopicKey";
    private const string PropTransportId = "TransportId";
    private readonly KeyMasker _masker = masker ?? KeyMasker.Off;

    // A custom factory cannot be told the cache keeps entries over a subscription gap, so it is handed a topic that hides one.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ITopic<ICacheEvent>, ITopic<ICacheEvent>> _gapFiltered = [];

    private long _writes;

    // Bound once: a method group allocates a delegate at every conversion.
    private Action<object?>? _refreshMetadata;

    /// <summary>How many local writes have committed, counted as each one returns: a caller that read it before its own read of the tier, and missed a value, sees it move once that value's write has returned.</summary>
    internal long Writes => Volatile.Read(ref _writes);

    protected TimeProvider Clock { get; } = clock;

    private ICacheEntrySizeProvider SizeProvider { get; } = memoryCacheOptions.SizeProvider ?? new DefaultCacheEntrySizeProvider();

    /// <summary>Generic over the options so the caches' option structs come in as they are, not boxed into the interface.</summary>
    public bool Set<TOptions>(TOptions options, ICacheEntry item, Type entryType, TimeSpan? maxExpiration)
        where TOptions : ICacheEntryOptions
    {
        ICacheChangeToken? token = null;
        try
        {
            var topic = topicProvider.Create(options.TopicKey);
            token = changeTokenFactory is IMaskedChangeTokenFactory masked
                ? masked.Create(options.CacheKey, topic, cacheName, entryType, _masker, options.CallerKey, ignoreSubscriptionGaps)
                : changeTokenFactory.Create(options.CacheKey, TopicForCustomFactory(topic), cacheName, entryType);
            // The null token never changes and owns nothing to dispose, so it is not wired to the entry: the wiring costs a token list, a callback list and a thread-pool task per eviction.
            var wired = !ReferenceEquals(token, NullCacheChangeToken.Instance);
            if (wired)
            {
                var state = new RefreshMetadataState(options.CacheKey, options.TopicKey, item, token, entryType, maxExpiration, options.CallerKey);
                token.RegisterChangeCallback(_refreshMetadata ??= RefreshMetadata, state);
            }

            // Filled in place rather than through MemoryCacheEntryOptions, which is copied into the entry and discarded.
            // The entry is committed when disposed, and only once its value is set, so a throw before that stores nothing.
            using (var entry = memoryCache.CreateEntry(options.CacheKey.Name))
            {
                entry.AbsoluteExpiration = GetCacheExpiration(options.Expiration, maxExpiration);
                if (wired)
                {
                    entry.ExpirationTokens.Add(token);
                    entry.RegisterPostEvictionCallback(PostEviction, token);
                }

                if (memoryCacheOptions.SizeLimit.HasValue)
                {
                    entry.Size = SizeProvider.GetSize(item);
                }
                entry.Value = item;
            }

            // The cache detaches, and so disposes, the token of an entry it replaces or removes. An entry that lost the commit to a concurrent write, or expired on insert, is released only by its eviction callback on the thread pool, and a rewritten key piles those up while the pool is busy: released here instead.
            if (wired && !(memoryCache.TryGetValue(options.CacheKey.Name, out var current) && ReferenceEquals(current, item)))
            {
                Release(token);
            }

            // After the commit, not before: a write that began before a reader counted, and committed after the reader missed it, must still move the count.
            Interlocked.Increment(ref _writes);
            return true;
        }
        catch (Exception ex)
        {
            Release(token);
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

    private static void PostEviction(object key, object? value, EvictionReason reason, object? state) =>
        Release(state);

    /// <summary>Unsubscribes the token. Disposing it again, as its eviction callback will, is a no-op.</summary>
    private static void Release(object? token)
    {
        if (token is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private ITopic<ICacheEvent> TopicForCustomFactory(ITopic<ICacheEvent> topic) =>
        ignoreSubscriptionGaps ? _gapFiltered.GetValue(topic, static t => new GapFilteringTopic(t)) : topic;

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
