namespace UiPath.Caching;

/// <summary>
/// <see cref="MemorySetCache"/> keys its snapshot on serialized members, and <c>byte[]</c> compares
/// by reference. Without structural equality every lookup misses, and
/// <see cref="MultilayerSetCache.ContainsItemAsync{T}"/> reports that as an authoritative answer
/// rather than falling through to the backing tier.
/// </summary>
internal sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
{
    public static readonly ByteArrayEqualityComparer Instance = new();

    private ByteArrayEqualityComparer()
    {
    }

    public bool Equals(byte[]? x, byte[]? y) =>
        ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

    public int GetHashCode(byte[]? obj)
    {
        if (obj is null)
        {
            return 0;
        }
        var hash = new HashCode();
        hash.AddBytes(obj);
        return hash.ToHashCode();
    }
}
