namespace UiPath.Caching.Migration;

/// <summary>Stored fields to values and back for one side of a hash cache: the in-place format for L, the policy's effective format for N. No type name is read from Redis.</summary>
internal interface IKeyMoveHashCodec<T>
    where T : class
{
    /// <summary>Whether the side stores a null field as an empty value, and an empty hash as its empty marker.</summary>
    bool CachesNulls { get; }

    /// <summary>Whether the side's hashes can hold the metadata of a write: false for a format with no field for it, such as the older in-place format, so a write that carries metadata is refused where this side is the one written.</summary>
    bool CarriesMetadata { get; }

    /// <summary>The fields the side's write stores for <paramref name="fields"/>; an in-place side stores user fields only. An empty list means the write stores no key.</summary>
    IReadOnlyList<KeyValuePair<string, byte[]>> EncodeFields(IDictionary<string, T?> fields, IDictionary<string, string?>? metadata);

    /// <param name="stored">The stored fields, system fields included.</param>
    /// <param name="capped">False reads the hash whatever its size, as a read that serves it does; true applies the caps of a promotion.</param>
    DecodedHash<T> DecodeFields(IReadOnlyCollection<KeyValuePair<string, byte[]>> stored, bool capped = true);
}
