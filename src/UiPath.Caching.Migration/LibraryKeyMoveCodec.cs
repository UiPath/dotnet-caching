namespace UiPath.Caching.Migration;

/// <summary>The key move codec of one side, over a cache's own write and read code.</summary>
/// <param name="policy">The write policy of each encode, read at that encode; null writes the cache's default format.</param>
internal sealed class LibraryKeyMoveCodec<T>(IByteCodecCache cache, int maxValueBytes, Func<WriteLease>? policy = null) : IKeyMoveCodec<T>
    where T : class
{
    public byte[] Encode(T value)
    {
        var bytes = Encode(value, out var lease);
        lease.Dispose();
        return bytes;
    }

    public byte[] Encode(T value, out WriteLease lease)
    {
        lease = policy?.Invoke() ?? default;
        try
        {
            return cache.Encode(value, lease.Policy);
        }
        catch
        {
            lease.Dispose();
            lease = default;
            throw;
        }
    }

    public byte[]? EncodeNull() => cache.CachesNulls ? [] : null;

    public Decoded<T> Decode(byte[] bytes)
    {
        try
        {
            // Quiet: a scan over junk counts it as a move class, not as a read miss. A null is the empty value and TooLarge is the stored length over the cap, as the inventory judges by length alone. Never Expired: an envelope's own deadline is not the library's to know.
            return cache.Decode<T>(bytes, null, quiet: true) switch
            {
                (SnapshotOutcome.Found, { } value) => bytes.Length > maxValueBytes ? new Decoded<T>(DecodeClass.TooLarge, value) : Decoded<T>.Of(value),
                (SnapshotOutcome.Found or SnapshotOutcome.Absent, _) when bytes.Length == 0 => new Decoded<T>(DecodeClass.Null, null),
                _ => new Decoded<T>(DecodeClass.Unreadable, null),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Decoded<T>(DecodeClass.Unreadable, null);
        }
    }
}
