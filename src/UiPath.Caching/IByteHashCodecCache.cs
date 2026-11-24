namespace UiPath.Caching;

/// <summary>What a hash cache stores for the fields of a hash and what its read makes of stored fields.</summary>
internal interface IByteHashCodecCache
{
    /// <summary>Whether the cache stores a null as an empty value.</summary>
    bool CachesNulls { get; }

    /// <summary>The (field, bytes) pairs a plain write of <paramref name="values"/> under <paramref name="policy"/> (null: the default policy) stores, the metadata and empty-hash fields included; expiry is not part of them, and an empty list means the write stores no key.</summary>
    IReadOnlyList<KeyValuePair<string, byte[]>> EncodeFields<T>(IDictionary<string, T?> values, IDictionary<string, string?>? metadata, CachePolicy? policy);

    /// <summary>The user fields only, as the hash of a client that keeps no system fields stores them: no metadata, no write count and no empty-hash field, whatever the cache's null setting.</summary>
    IReadOnlyList<KeyValuePair<string, byte[]>> EncodeUserFields<T>(IDictionary<string, T?> values, CachePolicy? policy);

    /// <summary>What a read makes of <paramref name="stored"/>; unreadable data is an outcome, and only a null argument or an uncacheable <typeparamref name="T"/> throws. It queues no rewrite, and <paramref name="quiet"/> also logs and counts nothing.</summary>
    HashDecode<T> DecodeFields<T>(IReadOnlyCollection<KeyValuePair<string, byte[]>> stored, CachePolicy? policy, bool quiet = false);
}
