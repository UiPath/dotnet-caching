using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache : RedisCacheBase, IHashCache, ISpanKeyHashCache, IVersionedHashCache, IInvalidationSink
{
    /// <summary>All fields and the absolute expiration in one atomic read-only call.</summary>
    private const string ReadWithExpirationScript = "local n = redis.call('HLEN', KEYS[1]) if n > tonumber(ARGV[1]) then return {n, redis.call('PEXPIRETIME', KEYS[1])} end return {redis.call('HGETALL', KEYS[1]), redis.call('PEXPIRETIME', KEYS[1])}";

    /// <summary>Times <c>HashFieldBatchSize</c>: the field count above which a hash is read by pages.</summary>
    private const int PagedReadFactor = 10;
    private const int SerializerFailed = int.MaxValue;

    // Not Name, which both Redis caches share: the serializer metrics tell a hash from a string.
    private const string MetricCache = "RedisHash";

    private static readonly TimeSpan ChunkedWriteOrphanLifetime = TimeSpan.FromMinutes(10);

    private readonly ILogger<RedisHashCache> _logger;
    private readonly Action<Exception> _logFailure;
    private readonly Action<Exception> _logReadScriptUnavailable;
    private readonly Action<Exception> _rewriteScriptRefused;
    private readonly ISerializerProxy<byte[]> _serializer;
    private readonly IMemorySerializerProxy? _memorySerializer;
    private readonly ICacheEntryFactory _cacheEntryFactory;
    private readonly IResiliencePipeline _read;
    private readonly IResiliencePipeline _write;
    private readonly IRedisKeyStrategy _redisKeyStrategy;
    private readonly CacheOptions _cacheOptions;
    private readonly bool _auditLargeValues;
    private readonly bool _cacheNullValues;
    private readonly int _fieldBatchSize;
    private ValueSerializerRegistry _valueSerializers = ValueSerializerRegistry.Empty;
    private ValueFormatResolver _formats;
    private IEffectiveFormat? _effectiveFormat;
    private ValueMigration? _migration;

    public RedisHashCache(
        IRedisConnector redis,
        ISerializerProxy<byte[]> serializer,
        IResiliencePipelineProvider resiliencePipelineProvider,
        ICachingTelemetryProvider telemetryProvider,
        RedisCacheOptions redisCacheOptions,
        CacheOptions cacheOptions,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        ILogger<RedisHashCache> logger,
        IKeyMaskingPolicy? keyMaskingPolicy = null)
        : base(redis, telemetryProvider, redisCacheOptions, cacheOptions, policyFactory, clock, keyMaskingPolicy)
    {
        _serializer = serializer;
        _memorySerializer = serializer as IMemorySerializerProxy;
        _formats = new ValueFormatResolver(serializer, ValueSerializerRegistry.Empty, DefaultPolicy);
        _logger = logger;
        _logFailure = LogRedisHashCacheException;
        _logReadScriptUnavailable = LogReadScriptUnavailable;
        _rewriteScriptRefused = RewriteScriptsRefused;
        _read = new ConnectingPipeline(redis, resiliencePipelineProvider.Get(ResiliencePipelineNames.Read));
        _write = new ConnectingPipeline(redis, resiliencePipelineProvider.Get(ResiliencePipelineNames.Write));
        _cacheOptions = cacheOptions;
        _cacheEntryFactory = redisCacheOptions.EntryFactory ?? new CacheEntryFactory();
        _redisKeyStrategy = (redisCacheOptions.RedisKeyStrategyFactory ?? new DefaultRedisKeyStrategyFactory()).Create(_cacheOptions, GetType());
        _cacheNullValues = redisCacheOptions.CacheNullValues;
        _fieldBatchSize = Math.Max(1, redisCacheOptions.HashFieldBatchSize);
        _auditLargeValues = _cacheOptions.AuditEnabled && _cacheOptions.LargeValueThreshold > 0;
    }

    public string Name => KnownCacheProviderNames.Redis;

    internal ValueSerializerRegistry ValueSerializers
    {
        get => _valueSerializers;
        init
        {
            _valueSerializers = value;
            _formats = new ValueFormatResolver(_serializer, value, DefaultPolicy, _effectiveFormat);
        }
    }

    internal IEffectiveFormat? EffectiveFormat
    {
        init
        {
            _effectiveFormat = value;
            _formats = new ValueFormatResolver(_serializer, _valueSerializers, DefaultPolicy, value);
        }
    }

    internal ValueMigration? Migration
    {
        init => _migration = value;
    }

    // For tests: runs after a chunked replacement filled its staging key, before the rename.
    internal Func<IDatabase, RedisKey, Task>? OnStaged { get; set; }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CachePolicy? policy, CancellationToken token = default)
    {
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetItemCoreAsync<T, ValueFormat>(cacheKey, field, format, token) : GetItemCoreAsync<T, PlainFormat>(cacheKey, field, default, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetCoreAsync<T, ValueFormat>(cacheKey, format, token) : GetCoreAsync<T, PlainFormat>(cacheKey, default, token);
    }

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default)
    {
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetFieldsCoreAsync<T, ValueFormat>(cacheKey, fields, format, token) : GetFieldsCoreAsync<T, PlainFormat>(cacheKey, fields, default, token);
    }

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetCacheEntryCoreAsync<T, ValueFormat>(cacheKey, format, token) : GetCacheEntryCoreAsync<T, PlainFormat>(cacheKey, default, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync<T, Func<CancellationToken, Task<IDictionary<string, T?>>>>(cacheKey, generator, static (g, ct) => g(ct), GetExpiration(policy), HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync<T, Func<CancellationToken, Task<IDictionary<string, T?>>>>(cacheKey, generator, static (g, ct) => g(ct), GetExpiration(expiration), HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync<T, Func<CancellationToken, Task<IDictionary<string, T?>>>>(cacheKey, generator, static (g, ct) => g(ct), GetExpiration(expiration), HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync<T, Func<CancellationToken, Task<IDictionary<string, T?>>>>(cacheKey, generator, static (g, ct) => g(ct), GetExpiration(expiration), setOption ?? HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync(cacheKey, state, generator, GetExpiration(policy), HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync(cacheKey, state, generator, GetExpiration(expiration), HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync(cacheKey, state, generator, GetExpiration(expiration), HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync(cacheKey, state, generator, GetExpiration(expiration), setOption ?? HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        return RunAsync(_read, StartOperation<T>(), static (s, token) => s.Self.Database.KeyExistsAsync(s.Key, s.Self.ReadFlagsFor(s.Key)).AsValueTask(), (Self: this, Key: redisKey), false, static hit => hit, _logFailure, token);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(policy), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) =>
        RefreshWithOptionsAsync<T>(cacheKey, options, policy, FormatFor(policy, forWrite: true).Writer, token);

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        NoteWritten(redisKey);
        return NoteWrittenAfterAsync(redisKey, RunAsync(_write, StartOperation<T>(), static (s, token) => s.Self.Database.KeyDeleteAsync(s.Key, CommandFlags.DemandMaster).AsValueTask(), (Self: this, Key: redisKey), false, static removed => removed, _logFailure, token));
    }

    public ValueTask<bool> RemoveFieldsAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        RemoveFieldsCoreAsync<T>(cacheKey, fields, versioned: false, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, values, GetExpiration(policy), policy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, values, GetExpiration(expiration), policy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, values, GetExpiration(expiration), policy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) =>
        SetCountingAsync(cacheKey, values, options, policy, report: null, token);

    public async ValueTask<VersionedWrite> SetVersionedAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, PeerFieldLimits limits, CancellationToken token = default)
    {
        var report = new WriteReport(limits);
        var written = await SetCountingAsync(cacheKey, values, options, policy, report, token).ConfigureAwait(false);
        return new VersionedWrite(written, report.Version, report.Fields);
    }

    public ValueTask<bool> RemoveFieldsVersionedAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        RemoveFieldsCoreAsync<T>(cacheKey, fields, versioned: true, token);

    public bool TryDecodeFields<T>(IReadOnlyDictionary<string, byte[]?> encoded, CachePolicy? policy, [MaybeNullWhen(false)] out Dictionary<string, T?> values)
    {
        try
        {
            // The held key's own policy decides the gate, as it does for a read. Where serializers are registered and that policy is not known, nothing is decoded.
            var readable = FormatFor(policy, forWrite: false).Readable;
            // With no policy, the default's own Read list would stand in for the key's, so only an unlimited one decodes.
            var ambiguous = _formats.Reader is not null && (readable is null || (policy is null && !ReferenceEquals(readable, ReadableFormats.Any)));
            var unreadable = 0;
            var decoded = new Dictionary<string, T?>(encoded.Count);
            foreach (var (field, bytes) in encoded)
            {
                if (KnownFieldNames.IsReserved(field))
                {
                    values = null;
                    return false;
                }

                if (ambiguous && bytes is { Length: > 0 })
                {
                    values = null;
                    return false;
                }

                decoded[field] = DeserializeField<T>(bytes is null ? RedisValue.Null : (byte[])bytes.Clone(), readable, ref unreadable);
                if (unreadable != 0)
                {
                    values = null;
                    return false;
                }
            }

            values = decoded;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            values = null;
            return false;
        }
    }

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return KeyTimeToLiveAsync(_read, StartOperation<T>(), _redisKeyStrategy, cacheKey, _logFailure, token);
    }

    public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return KeyExpireTimeAsync(_read, StartOperation<T>(), _redisKeyStrategy, cacheKey, _logFailure, token);
    }

    public ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        // No policy is known here, and the metadata was written by whichever policy wrote the hash: every registered format reads.
        var format = FormatFor(policy: null, forWrite: false);
        return format.Sniff
            ? GetInnerAsync<IDictionary<string, string?>, ValueFormat>(cacheKey, KnownFieldNames.MetadataKey, format with { Readable = ReadableFormats.Any }, token)
            : GetInnerAsync<IDictionary<string, string?>, PlainFormat>(cacheKey, KnownFieldNames.MetadataKey, default, token);
    }

    public ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) =>
        SetMetadataCoreAsync<T>(cacheKey, metadata, FormatFor(policy: null, forWrite: true).Writer, token);

    void IInvalidationSink.Invalidated(CacheKey key) => NoteInvalidated(_redisKeyStrategy, key);

    void IInvalidationSink.InvalidatedAll() => NoteAllInvalidated();

    /// <summary>Throws a transaction's own failure, not the cancellation of what it queued.</summary>
    private static async Task CommitAsync(ITransaction transaction, string what, RedisKey key, Task first, Task second)
    {
        bool committed;
        try
        {
            committed = await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch
        {
            first.Forget();
            second.Forget();
            throw;
        }

        if (!committed)
        {
            first.Forget();
            second.Forget();
            throw new RedisException($"{what} of {key} did not commit.");
        }

        await ObserveAllAsync([first, second]).ConfigureAwait(false);
    }

    private static async Task SettleAsync(List<Task> pending)
    {
        foreach (var task in pending)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Observed; the caller rethrows its own.
            }
        }
    }

    /// <summary>Awaits every task and rethrows the first failure.</summary>
    private static async Task ObserveAllAsync(List<Task> pending)
    {
        Exception? first = null;
        foreach (var task in pending)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                first ??= ex;
            }
        }

        if (first is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
        }
    }

    private static void QueueExpirationUpdate(ITransaction transaction, RedisKey redisKey, DateTimeOffset expiration)
    {
        if (expiration != DateTimeOffset.MaxValue)
        {
            transaction.KeyExpireAsync(redisKey, expiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget).Forget();
            return;
        }
        transaction.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget).Forget();
    }

    private static ImmutableDictionary<string, T?> Empty<T>() => ImmutableDictionary<string, T?>.Empty;

    private static DateTimeOffset? ToExpiration(long expireAt) =>
        // PEXPIRETIME answers -1 for a key without a TTL and -2 for a missing one.
        expireAt < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(expireAt);

    private static (HashEntry[] Entries, DateTimeOffset? Expiration) ReadReply(RedisResult reply)
    {
        var parts = ((RedisResult[]?)reply)!;
        var fields = ((RedisValue[]?)parts[0])!;
        var entries = new HashEntry[fields.Length / 2];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = new HashEntry(fields[2 * i], fields[(2 * i) + 1]);
        }

        return (entries, ToExpiration((long)parts[1]));
    }

    private static void Release(SerializedPayload[] payloads)
    {
        foreach (var payload in payloads)
        {
            payload.Dispose();
        }
    }

    private async ValueTask<bool> RefreshWithOptionsAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, ICacheValueSerializer? writer, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var expiration = GetExpiration(options, policy);
        var now = Clock.GetUtcNow();
        var ret = false;
        NoteWritten(redisKey);
        var operation = StartOperation<T>(nameof(RefreshAsync));
        try
        {
            if (expiration < now)
            {
                ret = await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyDeleteAsync(s.Key, CommandFlags.DemandMaster).AsValueTask();
                },
                (Self: this, Key: redisKey),
                default,
                token).ConfigureAwait(false);
            }
            else
            {
                await EnsureConnectedAsync(_write, token).ConfigureAwait(false);
                var transaction = Database.CreateTransaction();
                using (QueueMetadataWrite(transaction, Logged(cacheKey, redisKey, typeof(T)), redisKey, options.Metadata, writer))
                {
                    QueueExpirationUpdate(transaction, redisKey, expiration);

                    ret = await _write.ExecuteAsync(static (s, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return s.ExecuteAsync(CommandFlags.DemandMaster).AsValueTask();
                    },
                    transaction,
                    default,
                    token).ConfigureAwait(false);
                }

                if (!ret)
                {
                    LogRedisTransactionFailed();
                }
            }
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(ret);
        }

        return ret;
    }

    private async ValueTask<bool> SetMetadataCoreAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, ICacheValueSerializer? writer, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        NoteWritten(redisKey);
        var ret = false;
        var operation = StartOperation<T>(nameof(SetMetadataAsync));
        try
        {
            var keyExists = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.KeyExistsAsync(s.Key, s.Self.ReadFlagsFor(s.Key)).AsValueTask();
            },
            (Self: this, Key: redisKey),
            default,
            token).ConfigureAwait(false);
            if (keyExists)
            {
                if (metadata.Count > 0)
                {
                    var (metadataValue, payload) = SerializeFieldValue(metadata, writer);
                    if (_auditLargeValues)
                    {
                        AuditKeySize(Logged(cacheKey, redisKey, typeof(T)), KnownFieldNames.MetadataKey, metadataValue);
                    }

                    using (payload)
                    {
                        if (_cacheNullValues)
                        {
                            ret = await _write.ExecuteAsync(static async (s, token) =>
                            {
                                token.ThrowIfCancellationRequested();
                                var transaction = s.Self.Database.CreateTransaction();
                                transaction.AddCondition(Condition.KeyExists(s.Key));
                                transaction.HashSetAsync(s.Key, KnownFieldNames.MetadataKey, s.MetadataValue, When.Always, CommandFlags.DemandMaster).Forget();
                                return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
                            },
                            (Self: this, Key: redisKey, MetadataValue: metadataValue),
                            default,
                            token).ConfigureAwait(false);
                        }
                        else
                        {
                            ret = await _write.ExecuteAsync(static async (s, token) =>
                            {
                                token.ThrowIfCancellationRequested();
                                await s.Self.Database.HashSetAsync(s.Key, KnownFieldNames.MetadataKey, s.MetadataValue, When.Always, CommandFlags.DemandMaster).ConfigureAwait(false);
                                return true;
                            },
                            (Self: this, Key: redisKey, MetadataValue: metadataValue),
                            default,
                            token).ConfigureAwait(false);
                        }
                    }
                }
                else if (_cacheNullValues)
                {
                    ret = await _write.ExecuteAsync(static async (s, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        var transaction = s.Self.Database.CreateTransaction();
                        transaction.AddCondition(Condition.KeyExists(s.Key));
                        transaction.HashSetAsync(s.Key, KnownFieldNames.MetadataKey, RedisValue.EmptyString, When.Always, CommandFlags.DemandMaster).Forget();
                        return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
                    },
                    (Self: this, Key: redisKey),
                    default,
                    token).ConfigureAwait(false);
                }
                else
                {
                   ret = await _write.ExecuteAsync(static (s, token) =>
                   {
                       token.ThrowIfCancellationRequested();
                       return s.Self.Database.HashDeleteAsync(s.Key, KnownFieldNames.MetadataKey, CommandFlags.DemandMaster).AsValueTask();
                   },
                   (Self: this, Key: redisKey),
                   default,
                   token).ConfigureAwait(false);
                }
            }
            operation.Stop();

        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(ret);
        }

        return ret;
    }

    private async ValueTask<T?> GetItemCoreAsync<T, TFormat>(CacheKey cacheKey, string field, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        HashFields.ValidateFieldForRead(field);
        return await GetInnerAsync<T?, TFormat>(cacheKey, field, format, token);
    }

    private async ValueTask<IDictionary<string, T?>> GetCoreAsync<T, TFormat>(CacheKey cacheKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await GetInnerAsync<T?, TFormat>(cacheKey, format, token);
    }

    private async ValueTask<IDictionary<string, T?>> GetFieldsCoreAsync<T, TFormat>(CacheKey cacheKey, string[] fields, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await GetInnerAsync<T?, TFormat>(cacheKey, fields, format, token);
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryCoreAsync<T, TFormat>(CacheKey cacheKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await GetInnerCacheEntryAsync<T?, TFormat>(cacheKey, format, token);
    }

    private ValueTask<bool> RemoveFieldsCoreAsync<T>(CacheKey cacheKey, string[] fields, bool versioned, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        HashFields.ValidateFieldsForWrite(fields);
        var redisKey = ToRedisKey(cacheKey, token);
        if (fields.Length == 0)
        {
            return new ValueTask<bool>(true);
        }

        return RunAsync(_write, StartOperation<T>(), static (s, token) => s.Self.DeleteFieldsAsync(s.Key, s.Fields, s.Versioned, token), (Self: this, Key: redisKey, Fields: fields, Versioned: versioned), false, static removed => removed, _logFailure, token);
    }

    private ValueTask<bool> SetCountingAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, WriteReport? report, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        HashFields.ValidateForWrite(values);
        var redisKey = ToRedisKey(cacheKey, token);
        var writer = FormatFor(policy, forWrite: true).Writer;
        var requested = values.Count > 0 || _cacheNullValues ? options.Metadata : null;
        var metadata = MetadataToWrite(requested, writer);
        var expiration = GetExpiration(options, policy);
        var setOption = values.Count == 0 && _cacheNullValues ? HashCacheSetOption.KeyReplace : options.SetOption;

        // Everything that can throw runs before the fields and the metadata rent buffers, which SetInnerAsync then owns.
        var (entries, payloads) = SerializeFields(values, metadata, writer);
        if (requested is not null && metadata is null && setOption == HashCacheSetOption.HashReplace)
        {
            // A replacement too large for one transaction drops the stale metadata field after its chunks, since it is not atomic anyway.
            return entries.Length > _fieldBatchSize
                ? DropMetadataAfterAsync(redisKey, SetInnerAsync<T>(cacheKey, redisKey, entries, payloads, setOption, expiration, token, report), token)
                : SetInnerAsync<T>(cacheKey, redisKey, entries, payloads, setOption, expiration, token, report, dropMetadata: true);
        }

        return SetInnerAsync<T>(cacheKey, redisKey, entries, payloads, setOption, expiration, token, report);
    }

    /// <summary>Deletes the metadata field an earlier write left, after a chunked write: a replacement too large for one command is not atomic anyway.</summary>
    private async ValueTask<bool> DropMetadataAfterAsync(RedisKey redisKey, ValueTask<bool> write, CancellationToken token)
    {
        if (!await write.ConfigureAwait(false))
        {
            return false;
        }

        var dropped = false;
        try
        {
            dropped = await _write.ExecuteAsync(static async (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                await s.Self.Database.HashDeleteAsync(s.Key, KnownFieldNames.MetadataKey, CommandFlags.DemandMaster).ConfigureAwait(false);
                return true;
            },
            (Self: this, Key: redisKey),
            default,
            token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRedisHashCacheException(ex);
        }
        finally
        {
            NoteWritten(redisKey);
        }

        return dropped;
    }

    /// <summary>The hash, or its length when it is too large for one command.</summary>
    private async ValueTask<(HashEntry[] Entries, DateTimeOffset? Expiration)> ParseReplyAsync(RedisKey redisKey, RedisResult reply)
    {
        var parts = ((RedisResult[]?)reply)!;
        if (parts[0].Resp2Type != ResultType.Integer)
        {
            return ReadReply(reply);
        }

        // The pages are not one snapshot: a write between two of them can show in one and not the other.
        var fields = new Dictionary<RedisValue, RedisValue>();
        await foreach (var entry in Database.HashScanAsync(redisKey, pageSize: _fieldBatchSize, flags: ReadFlagsFor(redisKey)).ConfigureAwait(false))
        {
            // The pages are not one snapshot, so a version read among them says nothing of the fields read before it.
            if (!KnownFieldNames.IsVersion(entry.Name))
            {
                fields[entry.Name] = entry.Value;
            }
        }

        return (fields.Select(f => new HashEntry(f.Key, f.Value)).ToArray(), ToExpiration((long)parts[1]));
    }

    /// <summary>One HGETALL, or where scripts run, the script that pages a large hash.</summary>
    private async ValueTask<HashEntry[]> ReadAllEntriesAsync(RedisKey redisKey)
    {
        if (await TryReadScriptAsync(ReadWithExpirationScript, [redisKey], _logReadScriptUnavailable, [(long)_fieldBatchSize * PagedReadFactor]).ConfigureAwait(false) is { } reply)
        {
            return (await ParseReplyAsync(redisKey, reply).ConfigureAwait(false)).Entries;
        }

        return await Database.HashGetAllAsync(redisKey, ReadFlagsFor(redisKey)).ConfigureAwait(false);
    }

    private async ValueTask<RedisValue[]> HashGetFieldsAsync(RedisKey redisKey, string[] fields)
    {
        if (fields.Length <= _fieldBatchSize)
        {
            return await Database.HashGetAsync(redisKey, fields.Select(k => (RedisValue)k).ToArray(), ReadFlagsFor(redisKey)).ConfigureAwait(false);
        }

        var flags = ReadFlagsFor(redisKey);
        var pending = new List<Task<RedisValue[]>>();
        for (var start = 0; start < fields.Length; start += _fieldBatchSize)
        {
            var chunk = new RedisValue[Math.Min(_fieldBatchSize, fields.Length - start)];
            for (var i = 0; i < chunk.Length; i++)
            {
                chunk[i] = fields[start + i];
            }

            pending.Add(Database.HashGetAsync(redisKey, chunk, flags));
        }

        return (await Task.WhenAll(pending).ConfigureAwait(false)).SelectMany(values => values).ToArray();
    }

    private async ValueTask<IDictionary<string, T?>> GetOrAddCoreAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset effectiveExpiration, HashCacheSetOption setOption, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var (found, cached, unreadable) = await GetInnerWithFoundAsync<T?>(cacheKey, policy, token).ConfigureAwait(false);
        if (found)
        {
            return cached;
        }

        // A field this call cannot read would survive a write that only sets fields, and every read after would miss again until the key expires.
        if (unreadable && setOption == HashCacheSetOption.HashReplace)
        {
            setOption = HashCacheSetOption.KeyReplace;
        }

        LogCacheMissed(Logged(cacheKey, typeof(T)));
        var ret = await RunGeneratorAsync(cacheKey, state, generator, (policy ?? DefaultPolicy)?.FactoryTimeout, token).ConfigureAwait(false);
        if (ret.Count > 0)
        {
            var options = new HashCacheEntryOptions(effectiveExpiration, default, default, setOption);
            await SetAsync(cacheKey, ret, options, policy, token).ConfigureAwait(false);
        }
        else if (_cacheNullValues)
        {
            var options = new HashCacheEntryOptions(effectiveExpiration, default, default, setOption);
            await SetEmptyMarkerAsync<T>(cacheKey, options, policy, token).ConfigureAwait(false);
        }
        else
        {
            await RemoveAsync<T>(cacheKey, token).ConfigureAwait(false);
        }

        return ret;
    }

    private Task<IDictionary<string, T?>> RunGeneratorAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan? factoryTimeout, CancellationToken token) =>
        factoryTimeout is null || factoryTimeout.Value <= TimeSpan.Zero
            ? generator(state, token)
            : FactoryTimeout.RunAsync(ct => generator(state, ct), factoryTimeout, cacheKey, Name, Telemetry, token);

    private ValueTask<bool> SetEmptyMarkerAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token)
    {
        var redisKey = ToRedisKey(cacheKey, token);
        var writer = FormatFor(policy, forWrite: true).Writer;
        var expiration = GetExpiration(options, policy);
        if (options.Metadata is not { Count: > 0 } metadataValues)
        {
            return SetInnerAsync<T>(cacheKey, redisKey, [new HashEntry(KnownFieldNames.MetadataKey, RedisValue.EmptyString)], [], HashCacheSetOption.KeyReplace, expiration, token);
        }

        var (metadata, payload) = SerializeFieldValue(metadataValues, writer);
        return SetInnerAsync<T>(cacheKey, redisKey, [new HashEntry(KnownFieldNames.MetadataKey, metadata)], [payload], HashCacheSetOption.KeyReplace, expiration, token);
    }

    private async ValueTask<bool> RefreshCoreAsync<T>(CacheKey cacheKey, DateTimeOffset localExpiration, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        LogRefreshingKey(Logged(cacheKey, redisKey, typeof(T)), localExpiration);
        var ret = false;
        NoteWritten(redisKey);
        var operation = StartOperation<T>();
        try
        {
            ret = localExpiration != DateTimeOffset.MaxValue
                ? await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyExpireAsync(s.Key, s.LocalExpiration.UtcDateTime, s.Self.RefreshFlags).AsValueTask();
                },
                (Self: this, Key: redisKey, LocalExpiration: localExpiration),
                default,
                token).ConfigureAwait(false)
                : await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyPersistAsync(s.Key, s.Self.RefreshFlags).AsValueTask();
                },
                (Self: this, Key: redisKey),
                default,
                token).ConfigureAwait(false);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(ret);
        }

        return ret;
    }

    /// <summary>Returns the metadata's payload, which the caller disposes once the transaction has executed.</summary>
    private SerializedPayload QueueMetadataWrite(ITransaction transaction, LoggedKey logged, RedisKey redisKey, IDictionary<string, string?>? metadata, ICacheValueSerializer? writer)
    {
        metadata = MetadataToWrite(metadata, writer);
        if (metadata != null)
        {
            if (_cacheNullValues)
            {
                transaction.AddCondition(Condition.KeyExists(redisKey));
            }
            var (value, payload) = SerializeFieldValue(metadata, writer);
            if (_auditLargeValues)
            {
                AuditKeySize(logged, KnownFieldNames.MetadataKey, value);
            }

            try
            {
                var hashEntries = new[] { new HashEntry(KnownFieldNames.MetadataKey, value) };
                transaction.HashSetAsync(redisKey, hashEntries, CommandFlags.DemandMaster).Forget();
            }
            catch
            {
                payload.Dispose();
                throw;
            }
            return payload;
        }
        if (_cacheNullValues)
        {
            transaction.AddCondition(Condition.KeyExists(redisKey));
            var entries = new[] { new HashEntry(KnownFieldNames.MetadataKey, RedisValue.EmptyString) };
            transaction.HashSetAsync(redisKey, entries, CommandFlags.DemandMaster).Forget();
            return default;
        }
        transaction.HashDeleteAsync(redisKey, new RedisValue(KnownFieldNames.MetadataKey), CommandFlags.DemandMaster).Forget();
        return default;
    }

    private ValueTask<bool> SetCoreAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset effective, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        HashFields.ValidateForWrite(values);
        var redisKey = ToRedisKey(cacheKey, token);
        var (hashEntries, payloads) = SerializeFields(values, metadata: null, FormatFor(policy, forWrite: true).Writer);
        return SetInnerAsync<T>(cacheKey, redisKey, hashEntries, payloads, HashCacheSetOption.KeyReplace, effective, token);
    }

    /// <summary>With a non-JSON serializer the metadata field is left out when it is empty and nulls are not cached, so the hash reads back with none; the JSON default keeps writing it as it always has.</summary>
    private IDictionary<string, string?>? MetadataToWrite(IDictionary<string, string?>? metadata, ICacheValueSerializer? writer) =>
        writer is not null && !_cacheNullValues && metadata is { Count: 0 } ? null : metadata;

    /// <summary>One entry per field, then the metadata entry when there is metadata; the payloads go back once the write has completed.</summary>
    private (HashEntry[] Entries, SerializedPayload[] Payloads) SerializeFields<T>(IDictionary<string, T?> values, IDictionary<string, string?>? metadata, ICacheValueSerializer? writer)
    {
        var count = metadata is null ? values.Count : values.Count + 1;
        var entries = new HashEntry[count];
        var payloads = new SerializedPayload[count];
        var i = 0;
        try
        {
            var format = SerializerMetricsOn && !_valueSerializers.IsEmpty ? _valueSerializers.NameOf(writer) : null;
            foreach (var kv in values)
            {
                (var value, payloads[i]) = SerializeFieldValue(kv.Value, writer);
                entries[i++] = new HashEntry(kv.Key, value);
                if (format is not null)
                {
                    TrackSerializer(Metrics.SerializerWrite, format, MetricCache);
                }
            }
            if (metadata is not null)
            {
                (var value, payloads[i]) = SerializeFieldValue(metadata, writer);
                entries[i] = new HashEntry(KnownFieldNames.MetadataKey, value);
            }
        }
        catch
        {
            Release(payloads);
            throw;
        }
        return (entries, payloads);
    }

    /// <summary>Borrowed memory is safe here because every write awaits its command and the connection copies the value while writing it; pooled memory goes back only then.</summary>
    private (RedisValue Value, SerializedPayload Payload) SerializeFieldValue<T>(T? value, ICacheValueSerializer? writer)
    {
        if (_cacheNullValues && IsDefault(value))
        {
            return (RedisValue.EmptyString, default);
        }
        if (writer is not null)
        {
            var headered = SerializedPayload.Serialize(writer, _valueSerializers.FormatIdsOf(writer).Span, value!);
            return (headered.Memory, headered);
        }
        if (_memorySerializer is { } memory)
        {
            var payload = SerializedPayload.Serialize(memory, value);
            return (payload.Memory, payload);
        }
        return (_serializer.Serialize(value), default);
    }

    [SuppressMessage("SonarLint.Rule", "S3776")]
    private ICacheEntry<IDictionary<string, T?>> ParseCacheEntry<T>(CacheKey cacheKey, RedisKey redisKey, HashEntry[] hashEntries, DateTimeOffset? expireTime, ReadableFormats? readable, ref int unreadable)
    {
        if (hashEntries.Length == 0)
        {
            return Default<T>();
        }

        Dictionary<string, T?> values = [];
        IDictionary<string, string?>? extendedProps = default;
        FieldRewrites? rewrites = null;
        var hasEmptyMarker = false;
        long version = 0;

        for (var i = 0; i < hashEntries.Length; i++)
        {
            var hashEntry = hashEntries[i];
            var name = hashEntry.Name;
            var v = hashEntry.Value;
            if (KnownFieldNames.IsVersion(name))
            {
                version = v.TryParse(out long counted) ? counted : 0;
                continue;
            }


            if (KnownFieldNames.IsMetadata(name))
            {
                if (v.Length() == 0)
                {
                    hasEmptyMarker = true;
                }
                else
                {
                    extendedProps = ReadValue<IDictionary<string, string?>>(v, readable, ref unreadable);
                }
                continue;
            }

            values.Add(name.ToString(), DeserializeField<T>(name, v, readable, ref unreadable, ref rewrites));
        }

        if (unreadable != 0)
        {
            return Default<T>();
        }

        OfferRewrites<T>(redisKey, rewrites);

        if (values.Count == 0 && (!_cacheNullValues || !hasEmptyMarker))
        {
            return Default<T>();
        }

        var entry = _cacheEntryFactory.Create<IDictionary<string, T?>>(values, Clock.ToDateTimeOffset(expireTime), extendedProps);
        if (version > 0)
        {
            HashVersions.Stamp<T>(entry, version, null);
        }

        return entry;
    }

    // Bytes that did not come from Redis just now (the write-behind buffer, a peer's event): not counted as a read and never offered.
    private T? DeserializeField<T>(RedisValue value, ReadableFormats? readable, ref int unreadable)
    {
        if (value.IsNull)
        {
            return default;
        }
        if (value.Length() == 0)
        {
            return default;
        }
        var deserialized = ReadValue<T>(value, readable, ref unreadable);
        if (!_cacheNullValues && IsDefault(deserialized))
        {
            return default;
        }
        return deserialized;
    }

    /// <summary>A field a migrating call found in another readable format is noted in <paramref name="rewrites"/>, to be offered once the whole read is known to have hit.</summary>
    private T? DeserializeField<T>(RedisValue name, RedisValue value, ReadableFormats? readable, ref int unreadable, ref FieldRewrites? rewrites)
    {
        if (value.IsNull)
        {
            return default;
        }
        if (value.Length() == 0)
        {
            return default;
        }
        var deserialized = ReadValue<T>(value, readable, ref unreadable, out var sourceFormat);
        if (sourceFormat >= 0 && SerializerMetricsOn && !KnownFieldNames.IsSystemField(name))
        {
            TrackSerializer(Metrics.SerializerRead, _formats.Reader!.NameOf(sourceFormat), MetricCache);
        }

        if (!_cacheNullValues && IsDefault(deserialized))
        {
            return default;
        }

        if (sourceFormat >= 0 && !IsDefault(deserialized) && readable?.MigrateTo is { } target && _migration is { } migration)
        {
            var stored = (byte[]?)value;
            if (migration.Wants(target, stored!))
            {
                (rewrites ??= new(target)).TryAdd(name, stored!);
            }
        }

        return deserialized;
    }

    /// <summary>The value by its format header when the call sniffs, else through the proxy; a header no serializer owns, or the call's formats leave out, sets <paramref name="unreadable"/> to its id, and a serializer that throws to <see cref="SerializerFailed"/>; both read as nothing.</summary>
    private T? ReadValue<T>(RedisValue value, ReadableFormats? readable, ref int unreadable) =>
        ReadValue<T>(value, readable, ref unreadable, out _);

    /// <param name="sourceFormat">The format id the value was stored under, 0 for JSON; -1 when the call does not look for one, or the value was a miss.</param>
    private T? ReadValue<T>(RedisValue value, ReadableFormats? readable, ref int unreadable, out int sourceFormat)
    {
        sourceFormat = -1;
        if (readable is null)
        {
            return _serializer.Deserialize<T>(value);
        }

        var outcome = _formats.Reader!.Read<T>(((byte[]?)value)!, readable);
        if (outcome.IsMiss)
        {
            if (outcome.Failure is { } failure)
            {
                LogRedisHashCacheException(failure);
                unreadable = unreadable == 0 ? SerializerFailed : unreadable;
            }
            else
            {
                unreadable = outcome.UnreadableFormat != 0 ? outcome.UnreadableFormat : outcome.ExcludedFormat;
            }

            return default;
        }

        sourceFormat = outcome.SourceFormat;
        return outcome.Value;
    }

    private ValueFormat FormatFor(CachePolicy? policy, bool forWrite) => _formats.For(policy, forWrite);

    // The hash is left alone: a node that cannot read a format may be a stale one, and removing one field would leave the rest looking like a whole hash.
    private void TrackUnreadableRead(int unreadable)
    {
        if (unreadable is not 0 and not SerializerFailed)
        {
            TrackUnreadable(unreadable, Name);
        }
    }

    private bool IsCacheHit(RedisValue value) =>
        !value.IsNull && (value.Length() != 0 || _cacheNullValues);


    private async ValueTask<T?> GetInnerAsync<T, TFormat>(CacheKey cacheKey, string field, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        var redisKey = ToRedisKey(cacheKey, token);
        if (!IsConnected)
        {
            return default;
        }
        T? ret = default;
        bool found = false;
        var operation = StartOperation<T>(nameof(GetAsync));
        try
        {
            var value = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.HashGetAsync(s.Key, s.Field, s.Self.ReadFlagsFor(s.Key)).AsValueTask();
            },
            (Self: this, Key: redisKey, Field: field),
            RedisValue.Null,
            token).ConfigureAwait(false);
            var unreadable = 0;
            FieldRewrites? rewrites = null;
            ret = DeserializeField<T?>(field, value, format.Readable, ref unreadable, ref rewrites);
            found = unreadable == 0 && IsCacheHit(value);
            if (unreadable == 0)
            {
                OfferRewrites<T>(redisKey, rewrites);
            }

            TrackUnreadableRead(unreadable);
            operation.Stop();
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            TrackRead(operation, found, redisKey);
        }

        return ret;
    }

    private async ValueTask<IDictionary<string, T?>> GetInnerAsync<T, TFormat>(CacheKey cacheKey, string[] fields, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        if (fields == null || fields.Length == 0)
        {
            return await GetInnerAsync<T, TFormat>(cacheKey, format, token);
        }

        HashFields.ValidateFieldsForRead(fields);
        var redisKey = ToRedisKey(cacheKey, token);

        IDictionary<string, T?> ret = Empty<T?>();
        if (!IsConnected)
        {
            return ret;
        }
        var operation = StartOperation<T>(nameof(GetInnerAsync));
        bool found = false;
        try
        {
            var values = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.HashGetFieldsAsync(s.Key, s.Fields);
            },
            (Self: this, Key: redisKey, Fields: fields),
            [],
            token).ConfigureAwait(false);
            if (values.Length == fields.Length)
            {
                var unreadable = 0;
                var dict = new Dictionary<string, T?>(fields.Length);
                var anyPresent = false;
                FieldRewrites? rewrites = null;
                for (var i = 0; i < fields.Length; i++)
                {
                    var v = values[i];
                    dict.Add(fields[i], DeserializeField<T?>(fields[i], v, format.Readable, ref unreadable, ref rewrites));
                    anyPresent |= IsCacheHit(v);
                }

                if (unreadable == 0)
                {
                    ret = dict;
                    found = anyPresent;
                    OfferRewrites<T>(redisKey, rewrites);
                }

                TrackUnreadableRead(unreadable);
            }
            operation.Stop();
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            TrackRead(operation, found, redisKey);
        }

        return ret;
    }

    private async ValueTask<IDictionary<string, T?>> GetInnerAsync<T, TFormat>(CacheKey cacheKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        var redisKey = ToRedisKey(cacheKey, token);
        IDictionary<string, T?> ret = Empty<T?>();

        if (!IsConnected)
        {
            return ret;
        }

        var operation = StartOperation<T>(nameof(GetInnerAsync));
        bool found = false;
        try
        {
            var hashEntries = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.ReadAllEntriesAsync(s.Key);
            },
            (Self: this, Key: redisKey),
            [],
            token).ConfigureAwait(false);
            if (hashEntries.Length > 0)
            {
                var unreadable = 0;
                FieldRewrites? rewrites = null;
                (ret, found) = ReadFields<T>(hashEntries, format.Readable, ref unreadable, ref rewrites);
                if (unreadable == 0)
                {
                    OfferRewrites<T>(redisKey, rewrites);
                }

                TrackUnreadableRead(unreadable);
            }
            operation.Stop();
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            TrackRead(operation, found, redisKey);
        }

        return ret;
    }

    private ValueTask<(bool Found, IDictionary<string, T?> Values, bool Unreadable)> GetInnerWithFoundAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token)
    {
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetInnerWithFoundAsync<T, ValueFormat>(cacheKey, format, token) : GetInnerWithFoundAsync<T, PlainFormat>(cacheKey, default, token);
    }

    private async ValueTask<(bool Found, IDictionary<string, T?> Values, bool Unreadable)> GetInnerWithFoundAsync<T, TFormat>(CacheKey cacheKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        var redisKey = ToRedisKey(cacheKey, token);
        if (!IsConnected)
        {
            return (false, Empty<T?>(), false);
        }

        var operation = StartOperation<T>(nameof(GetOrAddAsync));
        bool found = false;
        var unreadable = 0;
        IDictionary<string, T?> ret = Empty<T?>();
        try
        {
            var hashEntries = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.ReadAllEntriesAsync(s.Key);
            },
            (Self: this, Key: redisKey),
            [],
            token).ConfigureAwait(false);

            if (hashEntries.Length == 0)
            {
                operation.Stop();
                return (false, ret, false);
            }

            FieldRewrites? rewrites = null;
            (ret, found) = ReadFields<T>(hashEntries, format.Readable, ref unreadable, ref rewrites);
            if (unreadable == 0)
            {
                OfferRewrites<T>(redisKey, rewrites);
            }

            TrackUnreadableRead(unreadable);
            operation.Stop();
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            TrackRead(operation, found, redisKey);
        }

        return (found, ret, unreadable != 0);
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetInnerCacheEntryAsync<T, TFormat>(CacheKey cacheKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        var ret = Default<T>();
        if(!IsConnected)
        {
            return ret;
        }

        var redisKey = ToRedisKey(cacheKey, token);
        var operation = StartOperation<T>(nameof(GetInnerCacheEntryAsync));
        try
        {
            ret = await GetCacheEntryForKeyAsync<T?, TFormat>(cacheKey, redisKey, format, token).ConfigureAwait(false);
            operation.Stop();
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            TrackRead(operation, ret.Found, redisKey);
        }

        return ret;
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryForKeyAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        token.ThrowIfCancellationRequested();
        // Per attempt, since a retry cannot re-run a drained transaction; boxed so the pipeline stays bool.
        var read = new StrongBox<StrongBox<(HashEntry[] Entries, DateTimeOffset? Expiration)>?>();
        var committed = await _read.ExecuteAsync(static async (s, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (await s.Self.TryReadScriptAsync(ReadWithExpirationScript, [s.Key], s.Self._logReadScriptUnavailable, [(long)s.Self._fieldBatchSize * PagedReadFactor]).ConfigureAwait(false) is { } reply)
            {
                Interlocked.CompareExchange(ref s.Read.Value, new(await s.Self.ParseReplyAsync(s.Key, reply).ConfigureAwait(false)), null);
                return true;
            }

            // An HGETALL with a PTTL in one round trip. Not atomic: a key that expires between them reads as fields without a TTL.
            var flags = s.Self.ReadFlagsFor(s.Key);
            var hashEntriesTask = s.Self.Database.HashGetAllAsync(s.Key, flags);
            var expireTimeToLiveTask = s.Self.Database.KeyTimeToLiveAsync(s.Key, flags);
            try
            {
                var entries = await hashEntriesTask.ConfigureAwait(false);
                var expiration = s.Self.Clock.ToDateTimeOffset(await expireTimeToLiveTask.ConfigureAwait(false));
                Interlocked.CompareExchange(ref s.Read.Value, new((entries, expiration)), null);
            }
            catch
            {
                hashEntriesTask.Forget();
                expireTimeToLiveTask.Forget();
                throw;
            }

            return true;
        },
        (Self: this, Key: redisKey, Read: read),
        default,
        token).ConfigureAwait(false);
        if (!committed || read.Value is not { } result)
        {
            throw new InvalidOperationException("Unable to read from redis");
        }

        var unreadable = 0;
        var entry = ParseCacheEntry<T>(cacheKey, redisKey, result.Value.Entries, result.Value.Expiration, format.Readable, ref unreadable);
        TrackUnreadableRead(unreadable);

        // A layered cache above reads this miss and writes the regenerated hash, and says by it whether a field is left to replace.
        return unreadable != 0 ? new UnreadableEntry<IDictionary<string, T?>>(entry) : entry;
    }

    /// <summary>Owns <paramref name="payloads"/>: they go back to the pool on every exit, including one before the command starts.</summary>
    private async ValueTask<bool> SetInnerAsync<T>(CacheKey cacheKey, RedisKey redisKey, HashEntry[] hashEntries, SerializedPayload[] payloads, HashCacheSetOption setOption, DateTimeOffset expiration, CancellationToken token, WriteReport? report = null, bool dropMetadata = false)
    {
        try
        {
            var now = Clock.GetUtcNow();
            var ret = false;
            if (token.IsCancellationRequested || !IsConnected)
            {
                token.ThrowIfCancellationRequested();
                return ret;
            }

            if (_auditLargeValues && expiration >= now)
            {
                AuditKeySize(Logged(cacheKey, redisKey, typeof(T)), hashEntries);
            }

            if (hashEntries.Length == 0 && expiration >= now && _cacheNullValues)
            {
                hashEntries = [new HashEntry(KnownFieldNames.MetadataKey, RedisValue.EmptyString)];
                setOption = HashCacheSetOption.KeyReplace;
            }

            // A chunked write is counted after its chunks land, not with them, so peers never get its fields.
            if (hashEntries.Length <= _fieldBatchSize)
            {
                report?.Capture(hashEntries, setOption);
            }

            NoteWritten(redisKey);
            var operation = StartOperation<T>(nameof(SetAsync));
            try
            {
                ret = await WriteAsync(redisKey, hashEntries, setOption, expiration, now, report, dropMetadata, token).ConfigureAwait(false);
                operation.Stop();
            }
            catch (Exception ex)
            {
                operation.Stop();
                LogRedisHashCacheException(ex);
            }
            finally
            {
                NoteWritten(redisKey);
                operation.Track(ret);
            }

            return ret;
        }
        finally
        {
            Release(payloads);
        }
    }

    /// <summary>A replacement needs a staging key on the key's slot; a merge always can.</summary>
    private bool TryChunkedWriteKeys(RedisKey redisKey, HashCacheSetOption setOption, out RedisKey staging)
    {
        staging = default;
        if (setOption == HashCacheSetOption.HashReplace)
        {
            return true;
        }

        var text = redisKey.ToString();
        var suffix = ":stage:" + Guid.NewGuid().ToString("N");
        if (RedisHashTag.HasValidTag(text))
        {
            staging = text + suffix;
        }
        else if (text.Length > 0 && RedisHashTag.ContainsNoBraces(text))
        {
            staging = "{" + text + "}" + suffix;
        }
        else
        {
            return false;
        }

        return SameSlot(redisKey, staging, _logger);
    }

    private async ValueTask<bool> WriteAsync(RedisKey redisKey, HashEntry[] hashEntries, HashCacheSetOption setOption, DateTimeOffset expiration, DateTimeOffset now, WriteReport? report, bool dropMetadata, CancellationToken token)
    {
        if (expiration < now || hashEntries.Length == 0)
        {
            return await DeleteKeyAsync(redisKey, token).ConfigureAwait(false);
        }

        if (hashEntries.Length > _fieldBatchSize && TryChunkedWriteKeys(redisKey, setOption, out var staging))
        {
            await EnsureConnectedAsync(_write, token).ConfigureAwait(false);
            return await _write.ExecuteAsync(static (s, token) => s.Self.WriteInChunksAsync(s.Key, s.Staging, s.Entries, s.Option, s.Expiration, s.Report, token), (Self: this, Key: redisKey, Staging: staging, Entries: hashEntries, Option: setOption, Expiration: expiration, Report: report), default, token).ConfigureAwait(false);
        }

        return await WriteInTransactionAsync(redisKey, hashEntries, setOption, expiration, report, dropMetadata, token).ConfigureAwait(false);
    }

    private ValueTask<bool> DeleteKeyAsync(RedisKey redisKey, CancellationToken token) =>
        _write.ExecuteAsync(
            static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.KeyDeleteAsync(s.Key, CommandFlags.DemandMaster).AsValueTask();
            },
            (Self: this, Key: redisKey),
            default,
            token);

    private async ValueTask<bool> WriteInTransactionAsync(RedisKey redisKey, HashEntry[] hashEntries, HashCacheSetOption setOption, DateTimeOffset expiration, WriteReport? report, bool dropMetadata, CancellationToken token)
    {
        await EnsureConnectedAsync(_write, token).ConfigureAwait(false);
        var transaction = Database.CreateTransaction();
        if (setOption == HashCacheSetOption.KeyReplace)
        {
            transaction.KeyDeleteAsync(redisKey).Forget();
        }

        transaction.HashSetAsync(redisKey, hashEntries, CommandFlags.DemandMaster).Forget();
        if (dropMetadata && setOption == HashCacheSetOption.HashReplace)
        {
            // In the write's own transaction, where a concurrent writer's newer metadata cannot be hit.
            transaction.HashDeleteAsync(redisKey, KnownFieldNames.MetadataKey, CommandFlags.DemandMaster).Forget();
        }

        Task<long>? counted = null;
        if (report is not null)
        {
            transaction.HashSetAsync(redisKey, KnownFieldNames.VersionKey, HashVersion.NewEpochSeed(), When.NotExists, CommandFlags.DemandMaster).Forget();
            counted = transaction.HashIncrementAsync(redisKey, KnownFieldNames.VersionKey, 1, CommandFlags.DemandMaster);
            counted.Forget();
        }

        if (expiration != DateTimeOffset.MaxValue)
        {
            await transaction.KeyExpireAsync(redisKey, expiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
        }
        else if (setOption == HashCacheSetOption.HashReplace)
        {
            // The key survives a HashReplace, and so would a TTL an earlier write gave it.
            transaction.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget).Forget();
        }

        var committed = await _write.ExecuteAsync(
            static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.ExecuteAsync(CommandFlags.DemandMaster).AsValueTask();
            },
            transaction,
            default,
            token).ConfigureAwait(false);
        if (!committed)
        {
            LogRedisTransactionFailed();
        }
        else if (counted is not null)
        {
            report!.Version = await VersionOrUncountedAsync(counted).ConfigureAwait(false);
        }

        return committed;
    }

    /// <summary>A merge is not atomic across chunks; a replacement fills a staging key and renames it over the key.</summary>
    private async ValueTask<bool> WriteInChunksAsync(RedisKey redisKey, RedisKey staging, HashEntry[] entries, HashCacheSetOption setOption, DateTimeOffset expiration, WriteReport? report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = setOption == HashCacheSetOption.HashReplace ? redisKey : staging;
        var pending = new List<Task>((entries.Length / _fieldBatchSize) + 1);
        try
        {
            // Each chunk with its lifetime: any of them can be the one that creates the key, after a deadline passed or an earlier chunk failed.
            for (var start = 0; start < entries.Length; start += _fieldBatchSize)
            {
                pending.Add(WriteWithLifetimeAsync(target, entries.AsSpan(start, Math.Min(_fieldBatchSize, entries.Length - start)).ToArray(), setOption, expiration));
            }

            await ObserveAllAsync(pending).ConfigureAwait(false);
            if (report is not null)
            {
                // Apart from the chunks, which are not atomic as a whole; the write reads as unversioned if the key is already gone.
                report.Version = await CountWriteAsync(target).ConfigureAwait(false);
            }

            if (setOption == HashCacheSetOption.KeyReplace)
            {
                if (OnStaged is { } staged)
                {
                    await staged(Database, staging).ConfigureAwait(false);
                }

                try
                {
                    await RenameOverAsync(staging, redisKey, expiration).ConfigureAwait(false);
                }
                catch (Exception renameFailed) when (expiration < Clock.GetUtcNow())
                {
                    return await DeleteExpiredReplacementAsync(redisKey, renameFailed).ConfigureAwait(false);
                }
            }

            return true;
        }
        catch
        {
            // A chunk still in flight reads the caller's buffers, which are released once this returns.
            await SettleAsync(pending).ConfigureAwait(false);
            if (setOption == HashCacheSetOption.KeyReplace)
            {
                DropStaging(staging);
            }

            throw;
        }
    }

    // The replacement expired before it took the key's place, its staging key with it: as an expired write does, it leaves the key gone.
    private async Task<bool> DeleteExpiredReplacementAsync(RedisKey redisKey, Exception renameFailed)
    {
        // A staging key that expired with its deadline is the expected cause; a timeout or a lost connection is not.
        if (renameFailed is RedisServerException && renameFailed.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
        {
            LogExpiredReplacement(renameFailed);
        }
        else
        {
            LogExpiredReplacementRenameFailed(renameFailed);
        }

        try
        {
            return await Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRedisHashCacheException(ex);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(renameFailed);
            throw;
        }
    }

    // The cleanup must not replace the failure that caused it.
    private void DropStaging(RedisKey staging)
    {
        try
        {
            Database.KeyDeleteAsync(staging, CommandFlags.DemandMaster | CommandFlags.FireAndForget).Forget();
        }
        catch (Exception ex)
        {
            LogRedisHashCacheException(ex);
        }
    }

    /// <summary>A hash with no expiry drops the staging key's orphan lifetime in the same transaction.</summary>
    private async Task RenameOverAsync(RedisKey staging, RedisKey redisKey, DateTimeOffset expiration)
    {
        if (expiration != DateTimeOffset.MaxValue)
        {
            await Database.KeyRenameAsync(staging, redisKey, When.Always, CommandFlags.DemandMaster).ConfigureAwait(false);
            return;
        }

        // Redis runs the rest of a transaction past a failed command: without the staging key, PERSIST would make the old hash immortal.
        var transaction = Database.CreateTransaction();
        transaction.AddCondition(Condition.KeyExists(staging));
        var renamed = transaction.KeyRenameAsync(staging, redisKey, When.Always, CommandFlags.DemandMaster);
        var persisted = transaction.KeyPersistAsync(redisKey, CommandFlags.DemandMaster);
        await CommitAsync(transaction, "The replacement", redisKey, renamed, persisted).ConfigureAwait(false);
    }

    private async Task WriteWithLifetimeAsync(RedisKey target, HashEntry[] chunk, HashCacheSetOption setOption, DateTimeOffset expiration)
    {
        var transaction = Database.CreateTransaction();
        var written = transaction.HashSetAsync(target, chunk, CommandFlags.DemandMaster);
        Task lifetime;
        if (expiration != DateTimeOffset.MaxValue)
        {
            lifetime = transaction.KeyExpireAsync(target, expiration.UtcDateTime, CommandFlags.DemandMaster);
        }
        else if (setOption == HashCacheSetOption.HashReplace)
        {
            lifetime = transaction.KeyPersistAsync(target, CommandFlags.DemandMaster);
        }
        else
        {
            lifetime = transaction.KeyExpireAsync(target, ChunkedWriteOrphanLifetime, CommandFlags.DemandMaster);
        }
        await CommitAsync(transaction, "A chunk", target, written, lifetime).ConfigureAwait(false);
    }

    /// <summary>Zero when the key is gone: counting would make a key of the count alone, with no lifetime. Zero too when the count cannot be made: the chunks it follows have landed.</summary>
    private async Task<long> CountWriteAsync(RedisKey redisKey)
    {
        try
        {
            var transaction = Database.CreateTransaction();
            transaction.AddCondition(Condition.KeyExists(redisKey));
            transaction.HashSetAsync(redisKey, KnownFieldNames.VersionKey, HashVersion.NewEpochSeed(), When.NotExists, CommandFlags.DemandMaster).Forget();
            var counted = transaction.HashIncrementAsync(redisKey, KnownFieldNames.VersionKey, 1, CommandFlags.DemandMaster);
            counted.Forget();
            return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false) ? await VersionOrUncountedAsync(counted).ConfigureAwait(false) : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogVersionNotCounted(ex);
            return 0;
        }
    }

    /// <summary>Zero, an uncounted write that peers drop on, when the count refuses to move: it stops at the 64-bit maximum while the write beside it still lands.</summary>
    private async Task<long> VersionOrUncountedAsync(Task<long> counted)
    {
        try
        {
            return await counted.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogVersionNotCounted(ex);
            return 0;
        }
    }

    private async Task<long> DeleteFieldsCountingAsync(RedisKey redisKey, RedisValue[] chunk)
    {
        var transaction = Database.CreateTransaction();
        transaction.AddCondition(Condition.KeyExists(redisKey));
        transaction.HashSetAsync(redisKey, KnownFieldNames.VersionKey, HashVersion.NewEpochSeed(), When.NotExists, CommandFlags.DemandMaster).Forget();
        transaction.HashIncrementAsync(redisKey, KnownFieldNames.VersionKey, 1, CommandFlags.DemandMaster).Forget();
        transaction.HashDeleteAsync(redisKey, chunk, CommandFlags.DemandMaster).Forget();
        await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
        return 0;
    }

    private ValueTask<bool> DeleteFieldsAsync(RedisKey redisKey, string[] fields, bool versioned, CancellationToken token)
    {
        NoteWritten(redisKey);
        return NoteWrittenAfterAsync(redisKey, DeleteFieldsCoreAsync(redisKey, fields, versioned, token));
    }

    private async ValueTask<bool> DeleteFieldsCoreAsync(RedisKey redisKey, string[] fields, bool versioned, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pending = new List<Task<long>>((fields.Length + _fieldBatchSize - 1) / _fieldBatchSize);
        for (var start = 0; start < fields.Length; start += _fieldBatchSize)
        {
            var chunk = new RedisValue[Math.Min(_fieldBatchSize, fields.Length - start)];
            for (var i = 0; i < chunk.Length; i++)
            {
                chunk[i] = fields[start + i];
            }

            pending.Add(versioned && start == 0 ? DeleteFieldsCountingAsync(redisKey, chunk) : Database.HashDeleteAsync(redisKey, chunk, CommandFlags.DemandMaster));
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
        if (versioned)
        {
            await RemoveCountOfEmptiedHashAsync(redisKey).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Redis deletes a hash whose last field goes; the count alone would keep the key, with its lifetime, as a hash no read can find a value in.</summary>
    private async Task RemoveCountOfEmptiedHashAsync(RedisKey redisKey)
    {
        var transaction = Database.CreateTransaction();
        transaction.AddCondition(Condition.HashLengthEqual(redisKey, 1));
        transaction.AddCondition(Condition.HashExists(redisKey, KnownFieldNames.VersionKey));
        transaction.HashDeleteAsync(redisKey, KnownFieldNames.VersionKey, CommandFlags.DemandMaster).Forget();
        await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
    }

    private RedisKey ToRedisKey(CacheKey cacheKey, CancellationToken token = default)
    {
        if (cacheKey.IsNull)
        {
            throw new ArgumentNullException(nameof(cacheKey));
        }
        token.ThrowIfCancellationRequested();
        return _redisKeyStrategy.GetRedisKey(cacheKey);
    }

    /// <summary>The hash's user fields, and whether they count as a hit: a value other than the null marker, or the empty marker while nulls are cached.</summary>
    private (Dictionary<string, T?> Values, bool Found) ReadFields<T>(HashEntry[] hashEntries, ReadableFormats? readable, ref int unreadable, ref FieldRewrites? rewrites)
    {
        var values = new Dictionary<string, T?>();
        var hasEmptyMarker = false;
        var anyValue = false;
        foreach (var hashEntry in hashEntries)
        {
            if (KnownFieldNames.IsMetadata(hashEntry.Name))
            {
                hasEmptyMarker = hashEntry.Value.Length() == 0;
                continue;
            }
            if (KnownFieldNames.IsSystemField(hashEntry.Name))
            {
                continue;
            }
            var v = hashEntry.Value;
            anyValue |= IsCacheHit(v);
            values.Add(hashEntry.Name.ToString(), DeserializeField<T>(hashEntry.Name, v, readable, ref unreadable, ref rewrites));
        }

        if (unreadable != 0)
        {
            return (new Dictionary<string, T?>(), false);
        }

        return (values, anyValue || (hasEmptyMarker && _cacheNullValues));
    }

    private TelemetryScope StartOperation<T>([CallerMemberName] string methodName = "") =>
        new(Telemetry, Clock, Name, methodName, typeof(T));


    private void AuditKeySize(LoggedKey key, HashEntry[] entries)
    {
        if (!_logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        foreach (var entry in entries)
        {
            AuditValue(key, entry.Name, entry.Value);
        }
    }

    private void AuditKeySize(LoggedKey key, RedisValue field, RedisValue value)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            AuditValue(key, field, value);
        }
    }

    private void AuditValue(LoggedKey key, RedisValue field, RedisValue value)
    {
        var valueLen = value.Length();
        if (valueLen > _cacheOptions.LargeValueThreshold)
        {
            LogLargeValueDetected(key, field.ToString(), valueLen);
        }
    }

    private ICacheEntry<IDictionary<string, T?>> Default<T>() => _cacheEntryFactory.Create(Empty<T?>(), DateTimeOffset.MinValue);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {CacheKey}")]
    private partial void LogCacheMissed(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refreshing key {RedisKey} at expiration {LocalExpiration}")]
    private partial void LogRefreshingKey(LoggedKey redisKey, DateTimeOffset localExpiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scripts are unavailable; hash cache reads now use an HGETALL and PTTL pair")]
    private partial void LogReadScriptUnavailable(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server refused the script that rewrites hash fields; rewrites now use a transaction per field")]
    private partial void LogRewriteScriptRefused(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RedisHashCache exception.")]
    private partial void LogRedisHashCacheException(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "A hash replacement expired before its rename; the key is deleted, as an expired write deletes it.")]
    private partial void LogExpiredReplacement(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A hash replacement past its deadline failed its rename for another reason than its expired staging key; the key is deleted, as an expired write deletes it.")]
    private partial void LogExpiredReplacementRenameFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A hash write landed without moving its version; the other nodes drop their copies of it.")]
    private partial void LogVersionNotCounted(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis transaction failed.")]
    private partial void LogRedisTransactionFailed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis large value detected for key {RedisKey}, field {Field}, length {Length}")]
    private partial void LogLargeValueDetected(LoggedKey redisKey, string field, long length);
}
