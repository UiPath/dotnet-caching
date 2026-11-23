using UiPath.Caching.Negotiation;
using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

public sealed class RedisCacheProvider : ICacheProvider
{
    private const string DefaultSweepGroup = "default";

    private readonly RedisCacheOptions _redisCacheOptions;
    private readonly CacheOptions _cacheOptions;
    private readonly IRedisConnector _redis;
    private readonly ISerializerProxy<byte[]> _serializerProxy;
    private readonly IResiliencePipelineProvider _resiliencePipelineProvider;
    private readonly ICachingTelemetryProvider _cachingTelemetryProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICachePolicyFactory _policyFactory;
    private readonly TimeProvider _clock;
    private readonly IKeyMaskingPolicy? _keyMaskingPolicy;
    private readonly ValueSerializerRegistry _valueSerializers;
    private readonly IEffectiveFormat? _effectiveFormat;
    private readonly ValueMigration? _migration;
    private readonly Lazy<RedisCache> _cache;
    private readonly Lazy<RedisHashCache> _hashCache;

    public RedisCacheProvider(
        IOptions<RedisCacheOptions> redisCacheOptions,
        IOptions<CacheOptions> cacheOptions,
        IRedisConnector redis,
        ISerializerProxy<byte[]> serializerProxy,
        IResiliencePipelineProvider resiliencePipelineProvider,
        ICachingTelemetryProvider cachingTelemetryProvider,
        ILoggerFactory loggerFactory,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        IKeyMaskingPolicy? keyMaskingPolicy = null)
        : this(
            redisCacheOptions,
            cacheOptions,
            redis,
            serializerProxy,
            resiliencePipelineProvider,
            cachingTelemetryProvider,
            loggerFactory,
            policyFactory,
            clock,
            keyMaskingPolicy,
            ValueSerializerRegistry.Empty)
    {
    }

    internal RedisCacheProvider(
        IOptions<RedisCacheOptions> redisCacheOptions,
        IOptions<CacheOptions> cacheOptions,
        IRedisConnector redis,
        ISerializerProxy<byte[]> serializerProxy,
        IResiliencePipelineProvider resiliencePipelineProvider,
        ICachingTelemetryProvider cachingTelemetryProvider,
        ILoggerFactory loggerFactory,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        IKeyMaskingPolicy? keyMaskingPolicy,
        ValueSerializerRegistry valueSerializers,
        IEffectiveFormat? effectiveFormat = null,
        ValueMigration? migration = null)
    {
        _valueSerializers = valueSerializers;
        _effectiveFormat = effectiveFormat;
        _migration = migration;
        _keyMaskingPolicy = keyMaskingPolicy;
        _clock = clock;
        _redisCacheOptions = redisCacheOptions.Value;
        _cacheOptions = cacheOptions.Value;
        _redis = redis;
        _serializerProxy = serializerProxy;
        _resiliencePipelineProvider = resiliencePipelineProvider;
        _cachingTelemetryProvider = cachingTelemetryProvider ?? NullTelemetryProvider.Instance;
        _loggerFactory = loggerFactory;
        _policyFactory = policyFactory;
        _cache = new Lazy<RedisCache>(() => BuildCache());
        _hashCache = new Lazy<RedisHashCache>(() => BuildHashCache());
        Enabled = _redisCacheOptions.Enabled;
    }

    public string Name => KnownCacheProviderNames.Redis;

    public bool Enabled { get; }

    public ICache CreateCache() =>
        _cache.Value;

    public IHashCache CreateHashCache() =>
        _hashCache.Value;

    /// <summary>Counts the strings and hashes whose key matches <paramref name="keyPattern"/>, a SCAN glob over the names the caches are called with, by the format they are stored in: <c>json</c>, a registered serializer's name, or <see cref="CacheValueFormats.UnknownName"/>; a hash is counted under each format one of its fields is in.</summary>
    public Task<IReadOnlyDictionary<string, long>> CountFormatsAsync(string keyPattern, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPattern);
        return CountFormatsByKeyAsync(new CacheKey(keyPattern, _cacheOptions.KeyCasing), token);
    }

    /// <summary>As <see cref="CountFormatsAsync(string, CancellationToken)"/>, for a pattern taken exactly as <see cref="CacheKey"/> holds it, such as a <see cref="CacheKey.Verbatim(string)"/> one.</summary>
    public Task<IReadOnlyDictionary<string, long>> CountFormatsByKeyAsync(CacheKey keyPattern, CancellationToken token = default)
    {
        return NewInventory().CountAsync(keyPattern, token);
    }

    /// <summary>
    /// As <see cref="CountFormatsByKeyAsync(CacheKey, CancellationToken)"/>, with the reason each value is where it is as well as its format: see <see cref="FormatInventoryReport"/>.
    /// Each string costs a <c>PTTL</c> and a headerless one a <c>STRLEN</c> besides the two-byte read, so run it off the request path, as the sweep is.
    /// </summary>
    public Task<FormatInventoryReport> CountFormatsByReasonAsync(CacheKey keyPattern, CancellationToken token = default) =>
        CountFormatsByReasonAsync(keyPattern, DefaultSweepGroup, token);

    /// <summary>As <see cref="CountFormatsByReasonAsync(CacheKey, CancellationToken)"/>, tracking <c>cache.format.inventory</c> under <paramref name="group"/> so an inventory of one set of keys does not replace the gauge of another.</summary>
    public async Task<FormatInventoryReport> CountFormatsByReasonAsync(CacheKey keyPattern, string group, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        var inventory = NewInventory();
        var report = await inventory.CountByReasonAsync(keyPattern, ValueMigration.MaxValueBytes, token).ConfigureAwait(false);

        // Every known reason is published, the absent ones as zero, so a reading that drained to nothing replaces the count the last inventory gave.
        var published = inventory.KnownReasons().ToDictionary(static reason => reason, static _ => 0L, StringComparer.Ordinal);
        foreach (var (reason, count) in report.ByReason)
        {
            published[reason] = count;
        }

        foreach (var (reason, count) in published)
        {
            try
            {
                _cachingTelemetryProvider.TrackMetric(Metrics.FormatInventory, count, [new("reason", reason), new("group", group)]);
            }
            catch (Exception ex)
            {
                _cachingTelemetryProvider.TryTrackException(ex);
            }
        }

        return report;
    }

    /// <summary>
    /// Rewrites the strings under <paramref name="keyPattern"/> that are stored in another format into the format the default policy writes, in place, for as long as
    /// <paramref name="options"/> allows; hashes are not swept. It is the pass <c>Serializer.MigrateOnRead</c> cannot make: values that are not read, have no TTL, or were skipped.
    /// </summary>
    /// <remarks>
    /// A value converts only if it comes out the same, by the compare-and-set that a read's rewrite uses: it replaces the exact bytes that were read, keeps the TTL, and
    /// never creates a key, so a writer or a delete that came meanwhile wins. It calls no origin and writes no event. Whether the node list allows the target format is asked
    /// again for each key, and the first refusal ends the pass; so does a Redis error that is not the key's own (a timeout, a dropped connection, a busy or loading server), which returns the report with a cursor to resume from. It shares the one-minute claim window and the byte budget of <c>MigrateOnRead</c>, but takes its own rate from
    /// <see cref="FormatSweepOptions.RatePerSecond"/>. Cancellation throws; use <see cref="FormatSweepOptions.Budget"/> to stop with a cursor. Throws <see cref="InvalidOperationException"/>
    /// when no serializer is registered or the default policy writes JSON, since there is no format to move toward.
    /// </remarks>
    public Task<FormatSweepReport> SweepFormatsAsync<T>(CacheKey keyPattern, FormatSweepOptions options, CancellationToken token = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_migration is not { } migration)
        {
            throw new InvalidOperationException("No value serializer is registered, so there is no format to sweep toward.");
        }

        var target = _valueSerializers.WriteSerializerFor(_policyFactory.Default, "default")
            ?? throw new InvalidOperationException("The default cache policy writes JSON, so there is no format to sweep toward.");
        var cache = _cache.Value;
        return new FormatSweeper(
            NewInventory(),
            _redis,
            migration,
            _valueSerializers,
            target,
            cache,
            (key, original, token) => cache.RewriteStoredAsync<T>(key, original, target, migration, token),
            original => migration.Rewriter.ReadsAsDefault<T>(original),
            _cachingTelemetryProvider,
            _clock).SweepAsync(keyPattern, options, token);
    }

    public void Dispose()
    {
        if (_cache.IsValueCreated)
        {
            _cache.Value.Dispose();
        }

        if (_hashCache.IsValueCreated)
        {
            _hashCache.Value.Dispose();
        }
    }

    internal static RedisCacheProvider Create(IServiceProvider services) =>
        new(
            services.GetRequiredService<IOptions<RedisCacheOptions>>(),
            services.GetRequiredService<IOptions<CacheOptions>>(),
            services.GetRequiredService<IRedisConnector>(),
            services.GetRequiredService<ISerializerProxy<byte[]>>(),
            services.GetRequiredService<IResiliencePipelineProvider>(),
            services.GetRequiredService<ICachingTelemetryProvider>(),
            services.GetRequiredService<ILoggerFactory>(),
            services.GetRequiredService<ICachePolicyFactory>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetService<IKeyMaskingPolicy>(),
            services.GetRequiredService<ValueSerializerRegistry>(),
            services.GetService<FormatNegotiation>()?.Negotiator,
            services.GetService<ValueMigration>());

    private FormatInventory NewInventory()
    {
        return new FormatInventory(
            _redis,
            _redisCacheOptions.KeyPrefix ?? string.Empty,
            _valueSerializers,
            RedisKeyStrategies.Resolve(_redisCacheOptions, _cacheOptions, typeof(RedisCache)),
            RedisKeyStrategies.Resolve(_redisCacheOptions, _cacheOptions, typeof(RedisHashCache)),
            _cacheOptions.KeyCasing);
    }

    private RedisCache BuildCache()
    {
        _valueSerializers.ValidateWriteNames(_policyFactory);
        return new(
            _redis,
            _serializerProxy,
            _resiliencePipelineProvider,
            _cachingTelemetryProvider,
            _redisCacheOptions,
            _cacheOptions,
            _policyFactory,
            _clock,
            _loggerFactory.Create<RedisCache>(),
            _keyMaskingPolicy)
        {
            ValueSerializers = _valueSerializers,
            EffectiveFormat = _effectiveFormat,
            Migration = _migration,
        };
    }

    private RedisHashCache BuildHashCache()
    {
        _valueSerializers.ValidateWriteNames(_policyFactory);
        return new(
            _redis,
            _serializerProxy,
            _resiliencePipelineProvider,
            _cachingTelemetryProvider,
            _redisCacheOptions,
            _cacheOptions,
            _policyFactory,
            _clock,
            _loggerFactory.Create<RedisHashCache>(),
            _keyMaskingPolicy)
        {
            ValueSerializers = _valueSerializers,
            EffectiveFormat = _effectiveFormat,
            Migration = _migration,
        };
    }
}
