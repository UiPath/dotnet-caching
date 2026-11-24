namespace UiPath.Caching;

/// <summary>What a cache stores for a value and what its snapshot read makes of stored bytes.</summary>
internal interface IByteCodecCache
{
    /// <summary>Whether the cache stores a null as an empty value.</summary>
    bool CachesNulls { get; }

    /// <summary>The bytes <c>WriteAsync</c> stores for <paramref name="value"/> under <paramref name="policy"/> (null: the default policy); expiry is not part of them.</summary>
    byte[] Encode<T>(T value, CachePolicy? policy);

    /// <summary>What <c>ReadSnapshotAsync</c> makes of <paramref name="stored"/>; unreadable bytes are an outcome, and only a null argument or an uncacheable <typeparamref name="T"/> throws. It queues no rewrite, and <paramref name="quiet"/> also logs and counts nothing.</summary>
    (SnapshotOutcome Outcome, T? Value) Decode<T>(byte[] stored, CachePolicy? policy, bool quiet = false);
}
