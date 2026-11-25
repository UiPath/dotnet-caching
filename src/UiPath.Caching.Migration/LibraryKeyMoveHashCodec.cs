using System.Text;

namespace UiPath.Caching.Migration;

/// <summary>The hash key move codec of one side, over a hash cache's own write and read code.</summary>
/// <param name="systemFields">False for the in-place side, whose hashes hold no metadata, write count or empty marker.</param>
/// <param name="policy">The write policy of each encode, read at that encode; null writes the cache's default format.</param>
internal sealed class LibraryKeyMoveHashCodec<T>(IByteHashCodecCache cache, int maxValueBytes, int maxHashFields, bool systemFields, Func<WriteLease>? policy = null) : IKeyMoveHashCodec<T>
    where T : class
{
    public bool CachesNulls => cache.CachesNulls;

    public bool CarriesMetadata => systemFields;

    public IReadOnlyList<KeyValuePair<string, byte[]>> EncodeFields(IDictionary<string, T?> fields, IDictionary<string, string?>? metadata)
    {
        var bytes = EncodeFields(fields, metadata, out var lease);
        lease.Dispose();
        return bytes;
    }

    public IReadOnlyList<KeyValuePair<string, byte[]>> EncodeFields(IDictionary<string, T?> fields, IDictionary<string, string?>? metadata, out WriteLease lease)
    {
        lease = policy?.Invoke() ?? default;
        try
        {
            return systemFields ? cache.EncodeFields(fields, metadata, lease.Policy) : cache.EncodeUserFields(fields, lease.Policy);
        }
        catch
        {
            lease.Dispose();
            lease = default;
            throw;
        }
    }

    public DecodedHash<T> DecodeFields(IReadOnlyCollection<KeyValuePair<string, byte[]>> stored, bool capped = true)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var length = stored.Count;

        // Above the cap by the field count alone, which is all the inventory knows: the hash is not read, so a hash that cannot be read is dropped like one that can.
        if (capped && KeyMoveHashSize.Of(length, maxHashFields) == DecodeClass.TooLarge)
        {
            return DecodedHash<T>.Of(DecodeClass.TooLarge, length);
        }

        try
        {
            // Quiet: a scan over junk counts it as a move class, not as a read miss.
            var decoded = cache.DecodeFields<T>(stored, null, quiet: true);
            var bytes = stored.Sum(e => (long)Encoding.UTF8.GetByteCount(e.Key) + e.Value.Length);
            return decoded.Outcome switch
            {
                SnapshotOutcome.Found when decoded.Values.Count != 0 => new DecodedHash<T>(capped && KeyMoveHashSize.ExceedsBytes(bytes, maxValueBytes) ? DecodeClass.TooLarge : DecodeClass.Ok, decoded.Values, decoded.Metadata, decoded.Version, length, bytes),
                SnapshotOutcome.Found or SnapshotOutcome.Absent => new DecodedHash<T>(DecodeClass.Null, decoded.Values, decoded.Metadata, decoded.Version, length, bytes),
                _ => DecodedHash<T>.Of(DecodeClass.Unreadable, length, bytes),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return DecodedHash<T>.Of(DecodeClass.Unreadable, length);
        }
    }
}
