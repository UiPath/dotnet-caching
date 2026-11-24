using StackExchange.Redis;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache : IByteCodecCache
{
    public bool CachesNulls => _cacheNullValues;

    public byte[] Encode<T>(T value, CachePolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(value);
        NotCacheableException.ThrowIfNotCacheable<T>();

        // The same format choice as WriteAsync, and the same SerializeValue behind it.
        var format = FormatFor(policy, forWrite: true);
        return format.IsDefault ? Stored(value, default(PlainFormat)) : Stored(value, format);
    }

    public (SnapshotOutcome Outcome, T? Value) Decode<T>(byte[] stored, CachePolicy? policy, bool quiet = false)
    {
        ArgumentNullException.ThrowIfNull(stored);
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff
            ? InterpretStored<T, ValueFormat>(default, stored, format, offerRewrite: false, quiet)
            : InterpretStored<T, PlainFormat>(default, stored, default, offerRewrite: false, quiet);
    }

    private byte[] Stored<T, TFormat>(T value, TFormat format)
        where TFormat : struct, IValueFormat
    {
        var (serialized, payload) = SerializeValue(value, format);
        using (payload)
        {
            // A pooled payload goes back to the pool at the end of this block, and the RedisValue of a payload that fills its rented array holds that array: copy before it is returned.
            return payload.Memory.IsEmpty
                ? (byte[]?)serialized ?? throw new InvalidOperationException("The serializer produced no bytes for the value.")
                : payload.Memory.ToArray();
        }
    }
}
