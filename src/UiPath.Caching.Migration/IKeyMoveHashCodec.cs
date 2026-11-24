namespace UiPath.Caching.Migration;

/// <summary>Stored fields to values and back for one side of a hash cache: the in-place format for L, the policy's effective format for N. No type name is read from Redis.</summary>
internal interface IKeyMoveHashCodec<T>
    where T : class
{
    /// <summary>Whether the side stores a null field as an empty value, and an empty hash as its empty marker.</summary>
    bool CachesNulls { get; }

    /// <summary>The fields the side's write stores for <paramref name="fields"/>; an in-place side stores user fields only. An empty list means the write stores no key.</summary>
    IReadOnlyList<KeyValuePair<string, byte[]>> EncodeFields(IDictionary<string, T?> fields, IDictionary<string, string?>? metadata);

    DecodedHash<T> DecodeFields(IReadOnlyCollection<KeyValuePair<string, byte[]>> stored);
}
