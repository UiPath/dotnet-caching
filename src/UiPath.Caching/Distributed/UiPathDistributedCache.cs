using System.Buffers.Text;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Caching.Distributed;

namespace UiPath.Caching.Distributed;

/// <summary>
/// <see cref="IDistributedCache"/> over an <see cref="IHashCache"/>. Keys are always case-sensitive;
/// each entry is a hash of <c>data</c> plus <c>absexp</c>/<c>sldexp</c> expiration metadata, so a
/// refresh reads the metadata without transferring the payload.
/// </summary>
/// <remarks>
/// Values travel as <see cref="ReadOnlyMemory{T}"/> of bytes rather than arrays. On the Redis tier that
/// lets a caller's buffer reach the wire with no array in between (<see cref="IMemorySerializerProxy"/>);
/// the memory tiers keep what they are handed, so a borrowed buffer is copied for them first — see the
/// sequence overload of <c>SetAsync</c>. What a tier hands back on a read is always a whole array, so the
/// array half of the contract stays copy-free too (<see cref="AsArray"/>).
/// </remarks>
internal sealed partial class UiPathDistributedCache : IDistributedCache
{
    private const string DataField = "data";
    private const string AbsoluteExpirationField = "absexp";
    private const string SlidingExpirationField = "sldexp";
    private const long Absent = -1;

    private static readonly string[] MetadataFields = [AbsoluteExpirationField, SlidingExpirationField];
    private static readonly string[] EntryFields = [DataField, AbsoluteExpirationField, SlidingExpirationField];

    /// <summary>The encoded <see cref="Absent"/> sentinel, shared: most writes carry it in at least one field.</summary>
    private static readonly ReadOnlyMemory<byte> AbsentTicks = "-1"u8.ToArray();

    private readonly IHashCache _cache;
    private readonly ICacheKeyStrategy _keyStrategy;
    private readonly CachePolicy? _policy;
    private readonly TimeSpan? _defaultEntryExpiration;
    private readonly bool _allowUnboundedEntries;
    private readonly bool _slideByRewrite;
    private readonly bool _tierRetainsValues;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <param name="keyStrategy">
    /// Applied to every composed key — <see cref="UiPathDistributedCacheOptions.CacheKeyStrategy"/> as resolved
    /// by registration. Applied here rather than through the backing provider's own
    /// <c>ICacheOptions.CacheKeyStrategy</c>, which the Redis tier's hash cache does not consult; going
    /// through the provider would make the stored key depend on the tier.
    /// </param>
    /// <param name="tierRetainsValues">
    /// The backing tier keeps a reference to the values it is handed — true of the memory-backed tiers, whose
    /// local layer stores the written dictionary as-is. A borrowed buffer then has to be copied before the
    /// write, because the caller reclaims it as soon as the call returns.
    /// </param>
    public UiPathDistributedCache(
        IHashCache cache,
        UiPathDistributedCacheOptions options,
        ICacheKeyStrategy keyStrategy,
        CachePolicy? policy,
        ILogger logger,
        TimeProvider clock,
        bool slideByRewrite = false,
        bool tierRetainsValues = false)
    {
        ArgumentNullException.ThrowIfNull(keyStrategy);
        _cache = cache;
        _keyStrategy = keyStrategy;
        _defaultEntryExpiration = options.DefaultEntryExpiration;
        _allowUnboundedEntries = options.AllowUnboundedEntries;
        _policy = policy;
        _slideByRewrite = slideByRewrite;
        _tierRetainsValues = tierRetainsValues;
        _logger = logger;
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public byte[]? Get(string key) =>
        GetAsync(key).GetAwaiter().GetResult();

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
        ReadPayloadAsync(key, token).AsTask();

    private async ValueTask<byte[]?> ReadPayloadAsync(string key, CancellationToken token) =>
        await ReadAsync(key, includeData: true, token).ConfigureAwait(false) is { } fields ? AsArray(Payload(fields)) : null;

    public void Refresh(string key) =>
        RefreshAsync(key).GetAwaiter().GetResult();

    public async Task RefreshAsync(string key, CancellationToken token = default) =>
        _ = await ReadAsync(key, includeData: false, token).ConfigureAwait(false);

    public void Remove(string key) =>
        RemoveAsync(key).GetAwaiter().GetResult();

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        var cacheKey = Encode(key);
        if (!await _cache.RemoveAsync<ReadOnlyMemory<byte>>(cacheKey, token).ConfigureAwait(false))
        {
            LogRemoveNotApplied(key);
        }
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        SetAsync(key, value, options).GetAwaiter().GetResult();

    /// <summary>
    /// The caller's array goes in as-is, the way the in-box memory cache keeps it too; wrapping it as memory
    /// puts every write on one path.
    /// </summary>
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WriteAsync(key, value, options, token).AsTask();
    }

    /// <summary>The one write path: resolves the deadline, encodes the metadata beside the payload and stores the three fields.</summary>
    private async ValueTask WriteAsync(string key, ReadOnlyMemory<byte> value, DistributedCacheEntryOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options);

        var cacheKey = Encode(key);
        var now = _clock.GetUtcNow();
        var absolute = ResolveAbsoluteExpiration(now, options);
        var sliding = options.SlidingExpiration;
        var fields = new Dictionary<string, ReadOnlyMemory<byte>>(3, StringComparer.Ordinal)
        {
            [DataField] = value,
            [AbsoluteExpirationField] = EncodeTicks(absolute?.UtcTicks),
            [SlidingExpirationField] = EncodeTicks(sliding?.Ticks),
        };

        var ttl = ResolveTimeToLive(now, sliding, absolute);
        if (!await StoreAsync(cacheKey, fields, ttl, token).ConfigureAwait(false))
        {
            LogWriteNotApplied(key);
        }
    }

    /// <summary>
    /// Writes the entry, honoring "until removed" literally: <see cref="DateTimeOffset.MaxValue"/> persists
    /// the key. Tested before the configured default so that default cannot silently override the flag.
    /// </summary>
    private ValueTask<bool> StoreAsync(
        CacheKey cacheKey,
        IDictionary<string, ReadOnlyMemory<byte>> fields,
        TimeSpan? ttl,
        CancellationToken token)
    {
        if (ttl is null && _allowUnboundedEntries)
        {
            return _cache.SetAsync(cacheKey, fields, DateTimeOffset.MaxValue, _policy, token);
        }

        // Caller TTL, else this adapter's configured default. With neither, the provider's own
        // default applies — which is what the overload carrying no expiration asks for.
        return (ttl ?? _defaultEntryExpiration) is { } duration
            ? _cache.SetAsync(cacheKey, fields, duration, _policy, token)
            : _cache.SetAsync(cacheKey, fields, _policy, token);
    }

    /// <summary>Composes the storage key by running the configured strategy over the validated caller key.</summary>
    private CacheKey Encode(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("The cache key must contain at least one non-whitespace character.", nameof(key));
        }

        var cacheKey = new CacheKey(key, CacheKeyCasing.Sensitive);
        var composed = _keyStrategy.GetCacheKey<ReadOnlyMemory<byte>>(cacheKey);
        if (composed.IsNull)
        {
            throw new InvalidOperationException(
                $"The cache key strategy configured on {nameof(UiPathDistributedCacheOptions)}.{nameof(UiPathDistributedCacheOptions.CacheKeyStrategy)} returned an empty key.");
        }

        if (composed.Casing != CacheKeyCasing.Sensitive)
        {
            throw new InvalidOperationException(
                $"The cache key strategy configured on {nameof(UiPathDistributedCacheOptions)}.{nameof(UiPathDistributedCacheOptions.CacheKeyStrategy)} returned a {composed.Casing} key, which lowercases the caller's key. {nameof(IDistributedCache)} keys are case-significant; build the result with {nameof(CacheKey)}.{nameof(CacheKey.WithName)} so the mode carries over.");
        }

        return composed;
    }

    /// <summary>
    /// Shared read path for Get, Refresh and TryGet: resolves the entry and slides it when due. Null is a
    /// miss; a hit hands back the fields that were read, so the caller decides how to shape the payload —
    /// and a hit whose payload is empty stays distinguishable from a miss.
    /// </summary>
    private async ValueTask<IDictionary<string, ReadOnlyMemory<byte>>?> ReadAsync(string key, bool includeData, CancellationToken token)
    {
        var cacheKey = Encode(key);
        var fields = await _cache
            .GetAsync<ReadOnlyMemory<byte>>(cacheKey, FieldsToRead(includeData), _policy, token)
            .ConfigureAwait(false);
        if (fields is null || !TryDecodeMetadata(fields, out var metadata))
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        if (IsExpired(metadata.AbsoluteExpiration, now))
        {
            return null;
        }

        if (metadata.SlidingExpiration is { } window)
        {
            var target = AddClamped(now, window.Ticks);
            if (metadata.AbsoluteExpiration is { } cap && cap < target)
            {
                target = cap;
            }

            await SlideAsync(cacheKey, fields, target, token).ConfigureAwait(false);
        }

        return fields;
    }

    /// <summary>
    /// Slide-by-rewrite writes the entry back, so it needs the payload in hand even on a refresh; that
    /// tier's read is in-process, which makes pulling every field free.
    /// </summary>
    private string[] FieldsToRead(bool includeData) =>
        includeData || _slideByRewrite ? EntryFields : MetadataFields;

    /// <summary>
    /// Past its absolute deadline. Reported as a miss and left to expire on its own: deleting it here would
    /// race a writer that has just replaced the value.
    /// </summary>
    private static bool IsExpired(DateTimeOffset? absolute, DateTimeOffset now) =>
        absolute is { } cap && cap <= now;

    /// <summary>
    /// The stored payload. Metadata without a data field is a hit with an empty payload, because the hash
    /// layer reports a zero-length value as absent.
    /// </summary>
    private static ReadOnlyMemory<byte> Payload(IDictionary<string, ReadOnlyMemory<byte>> fields) =>
        fields.TryGetValue(DataField, out var data) ? data : default;

    /// <summary>
    /// The array behind the memory when the memory spans all of it — which is what every tier hands back:
    /// the raw serializer wraps whole arrays and the memory tiers hold what a write gave them. So the array
    /// half of the contract returns the stored array as it always has, without a copy; a partial view, which
    /// nothing here produces, is copied rather than exposed along with its neighbours.
    /// </summary>
    private static byte[] AsArray(ReadOnlyMemory<byte> payload) =>
        MemoryMarshal.TryGetArray(payload, out var segment)
            && segment.Array is { } array
            && segment.Offset == 0
            && segment.Count == array.Length
            ? array
            : payload.ToArray();

    /// <summary>
    /// Extends the entry's deadline. Memory-backed tiers write it back instead of refreshing, because their
    /// refresh evicts the local entry and the inner cache cannot restore it.
    /// </summary>
    private async ValueTask SlideAsync(
        CacheKey cacheKey,
        IDictionary<string, ReadOnlyMemory<byte>> fields,
        DateTimeOffset target,
        CancellationToken token)
    {
        if (_slideByRewrite)
        {
            _ = await _cache.SetAsync(cacheKey, fields, new HashCacheEntryOptions(target), _policy, token).ConfigureAwait(false);
            return;
        }

        _ = await _cache.RefreshAsync<ReadOnlyMemory<byte>>(cacheKey, target, _policy, token).ConfigureAwait(false);
    }

    /// <summary>Expiration metadata as written, decoded once. Null means the sentinel: that deadline was not set.</summary>
    private readonly record struct EntryMetadata(DateTimeOffset? AbsoluteExpiration, TimeSpan? SlidingExpiration);

    /// <summary>
    /// Decodes both expiration fields, or reports a miss. The accepted values are exactly the ones a write can
    /// produce, so a stored entry always round-trips and anything else — a field the hash layer returned empty
    /// because the key is absent, text that does not parse, or a number outside the field's range — is a miss.
    /// Presence and meaning are settled in this one pass on purpose: deciding them separately let a value
    /// satisfy the presence test and then decode to "no expiration", which serves the payload as an entry that
    /// never expires.
    /// </summary>
    private static bool TryDecodeMetadata(IDictionary<string, ReadOnlyMemory<byte>> fields, out EntryMetadata metadata)
    {
        metadata = default;
        if (!TryDecodeTicks(fields, AbsoluteExpirationField, DateTime.MaxValue.Ticks, out var absoluteTicks)
            || !TryDecodeTicks(fields, SlidingExpirationField, TimeSpan.MaxValue.Ticks, out var slidingTicks))
        {
            return false;
        }

        metadata = new EntryMetadata(
            absoluteTicks is { } deadline ? new DateTimeOffset(deadline, TimeSpan.Zero) : null,
            slidingTicks is { } window ? new TimeSpan(window) : null);
        return true;
    }

    /// <summary>
    /// One field. True with a value, true with null for the <see cref="Absent"/> sentinel, false when the field
    /// holds something no write could have produced. A write emits either the sentinel or a strictly positive
    /// tick count within the field's range: an absolute deadline is required to be in the future, and
    /// <see cref="DistributedCacheEntryOptions.SlidingExpiration"/> only permits positive durations. Parsed
    /// straight from the bytes: only a leading sign is tolerated around the digits, because that is all
    /// <see cref="EncodeTicks"/> emits, and the whole field has to be consumed.
    /// </summary>
    private static bool TryDecodeTicks(IDictionary<string, ReadOnlyMemory<byte>> fields, string field, long maxTicks, out long? ticks)
    {
        ticks = null;
        if (!fields.TryGetValue(field, out var raw)
            || raw.IsEmpty
            || !Utf8Parser.TryParse(raw.Span, out long value, out var consumed)
            || consumed != raw.Length)
        {
            return false;
        }

        if (value == Absent)
        {
            return true;
        }

        if (value < 1 || value > maxTicks)
        {
            return false;
        }

        ticks = value;
        return true;
    }

    /// <summary>Formats straight to bytes, sized to the digits; the sentinel is shared rather than encoded per write.</summary>
    private static ReadOnlyMemory<byte> EncodeTicks(long? ticks)
    {
        if (ticks is not { } value)
        {
            return AbsentTicks;
        }

        Span<byte> digits = stackalloc byte[20];   // long.MinValue is 20 characters
        Utf8Formatter.TryFormat(value, digits, out var written);
        return digits[..written].ToArray();
    }

    private static DateTimeOffset AddClamped(DateTimeOffset now, long ticks)
    {
        var remaining = DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks;
        return ticks >= remaining ? DateTimeOffset.MaxValue : now.AddTicks(ticks);
    }

    private static TimeSpan? ResolveTimeToLive(DateTimeOffset now, TimeSpan? sliding, DateTimeOffset? absolute)
    {
        var remaining = absolute is { } cap ? cap - now : (TimeSpan?)null;
        return (sliding, remaining) switch
        {
            ({ } window, { } left) => TimeSpan.FromTicks(Math.Min(window.Ticks, left.Ticks)),
            ({ } window, null) => window,
            (null, { } left) => left,
            _ => null,
        };
    }

    private static DateTimeOffset? ResolveAbsoluteExpiration(DateTimeOffset now, DistributedCacheEntryOptions options)
    {
        if (options.AbsoluteExpiration is { } absolute)
        {
            return absolute <= now
                ? throw new ArgumentOutOfRangeException(nameof(options), absolute, "The absolute expiration must be in the future.")
                : absolute;
        }

        return options.AbsoluteExpirationRelativeToNow is { } relative ? AddClamped(now, relative.Ticks) : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Distributed cache write for key {Key} was not applied by the backing cache.")]
    private partial void LogWriteNotApplied(string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Distributed cache remove for key {Key} reported no change.")]
    private partial void LogRemoveNotApplied(string key);
}
