namespace UiPath.Caching;

/// <summary>What <c>GetOrAddResultAsync</c> returns: the value, when its entry expires (null when unknown or never), and whether this call's generator produced it.</summary>
/// <param name="Value">The cached or generated value.</param>
/// <param name="Expiration">When the entry expires; null when the cache does not know or the entry has no expiry.</param>
/// <param name="WasGenerated">True only when this call's generator produced the value; false for a hit and for a call that waited on a concurrent caller's generation.</param>
public readonly record struct CacheResult<T>(T? Value, DateTimeOffset? Expiration, bool WasGenerated);
