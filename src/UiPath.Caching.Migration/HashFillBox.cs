namespace UiPath.Caching.Migration;

/// <summary>Where a hash fill that lost leaves the hash that was stored, so the router keeps that one.</summary>
internal sealed class HashFillBox<T>
    where T : class
{
    /// <summary>The fill lost to what the key holds, whatever that is: a hash, a cached empty hash or unreadable data.</summary>
    public bool Lost { get; set; }

    /// <summary>The winner is a cached empty hash, which is the answer for the key.</summary>
    public bool CachedEmpty { get; set; }

    public IDictionary<string, T?>? Stored { get; set; }

    /// <summary>The key holds data the cache cannot read.</summary>
    public bool Unreadable { get; set; }

    /// <summary>When the stored hash expires, as the winner's own read reported it; null for none.</summary>
    public DateTimeOffset? StoredExpiration { get; set; }
}
