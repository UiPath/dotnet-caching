using System.Text;

namespace UiPath.Caching.Migration;

/// <summary>The hash key move codec of one side, over a hash cache's own write and read code.</summary>
/// <param name="systemFields">False for the in-place side, whose hashes hold no metadata, write count or empty marker.</param>
internal sealed class LibraryKeyMoveHashCodec<T>(IByteHashCodecCache cache, int maxValueBytes, int maxHashFields, bool systemFields) : IKeyMoveHashCodec<T>
    where T : class
{
    public bool CachesNulls => cache.CachesNulls;

    public IReadOnlyList<KeyValuePair<string, byte[]>> EncodeFields(IDictionary<string, T?> fields, IDictionary<string, string?>? metadata) =>
        systemFields ? cache.EncodeFields(fields, metadata, null) : cache.EncodeUserFields(fields, null);

    public DecodedHash<T> DecodeFields(IReadOnlyCollection<KeyValuePair<string, byte[]>> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var length = stored.Count;

        // Above the cap by the field count alone, which is all the inventory knows: the hash is not read, so a hash that cannot be read is dropped like one that can.
        if (KeyMoveHashSize.Of(length, maxHashFields) == DecodeClass.TooLarge)
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
                SnapshotOutcome.Found when decoded.Values.Count == 0 => DecodedHash<T>.Of(DecodeClass.Null, length, bytes),
                SnapshotOutcome.Found => new DecodedHash<T>(KeyMoveHashSize.ExceedsBytes(bytes, maxValueBytes) ? DecodeClass.TooLarge : DecodeClass.Ok, decoded.Values, decoded.Metadata, decoded.Version, length, bytes),
                SnapshotOutcome.Absent => DecodedHash<T>.Of(DecodeClass.Null, length, bytes),
                _ => DecodedHash<T>.Of(DecodeClass.Unreadable, length, bytes),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return DecodedHash<T>.Of(DecodeClass.Unreadable, length);
        }
    }
}
