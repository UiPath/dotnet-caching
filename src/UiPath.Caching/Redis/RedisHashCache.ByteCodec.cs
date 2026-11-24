using StackExchange.Redis;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache : IByteHashCodecCache
{
    public bool CachesNulls => _cacheNullValues;

    public IReadOnlyList<KeyValuePair<string, byte[]>> EncodeFields<T>(IDictionary<string, T?> values, IDictionary<string, string?>? metadata, CachePolicy? policy)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        HashFields.ValidateForWrite(values);

        // The same decisions as a write: the writer of the policy, the metadata it asks for, and the empty marker of a hash with nothing in it.
        var writer = FormatFor(policy, forWrite: true).Writer;
        var (entries, payloads) = SerializeFields(values, MetadataToWrite(MetadataRequested(values.Count, metadata), writer), writer);
        try
        {
            return Stored(entries.Length == 0 && _cacheNullValues ? EmptyMarkerEntries() : entries);
        }
        finally
        {
            Release(payloads);
        }
    }

    public IReadOnlyList<KeyValuePair<string, byte[]>> EncodeUserFields<T>(IDictionary<string, T?> values, CachePolicy? policy)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        HashFields.ValidateForWrite(values);
        var (entries, payloads) = SerializeFields(values, metadata: null, FormatFor(policy, forWrite: true).Writer);
        try
        {
            return Stored(entries);
        }
        finally
        {
            Release(payloads);
        }
    }

    public HashDecode<T> DecodeFields<T>(IReadOnlyCollection<KeyValuePair<string, byte[]>> stored, CachePolicy? policy, bool quiet = false)
    {
        ArgumentNullException.ThrowIfNull(stored);
        NotCacheableException.ThrowIfNotCacheable<T>();
        try
        {
            var format = FormatFor(policy, forWrite: false);
            var entries = new HashEntry[stored.Count];
            var i = 0;
            foreach (var (field, bytes) in stored)
            {
                entries[i++] = new HashEntry(field, bytes);
            }

            var unreadable = 0;
            FieldRewrites? rewrites = null;
            var parsed = ParseFields<T>(entries, format.Sniff ? format.Readable : null, ref unreadable, ref rewrites, quiet);
            if (unreadable != 0)
            {
                if (!quiet)
                {
                    TrackUnreadableRead(unreadable);
                }

                return HashDecode<T>.Unreadable(entries.Length);
            }

            // A hit as the read counts one: a field, or the empty marker while nulls are cached.
            var found = parsed.Values.Count > 0 || (_cacheNullValues && parsed.HasEmptyMarker);
            return new HashDecode<T>(found ? SnapshotOutcome.Found : SnapshotOutcome.Absent, parsed.Values, parsed.Metadata, parsed.HasEmptyMarker, parsed.Version, entries.Length);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!quiet)
            {
                LogRedisHashCacheException(ex);
            }

            return HashDecode<T>.Unreadable(stored.Count);
        }
    }

    // Copied: a payload that fills its rented array is lent to the entry, and goes back to the pool when the caller releases it.
    private static KeyValuePair<string, byte[]>[] Stored(HashEntry[] entries)
    {
        var stored = new KeyValuePair<string, byte[]>[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            stored[i] = new KeyValuePair<string, byte[]>(entries[i].Name.ToString(), ((ReadOnlyMemory<byte>)entries[i].Value).ToArray());
        }

        return stored;
    }
}
