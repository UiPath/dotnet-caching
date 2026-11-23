namespace UiPath.Caching.Migration;

/// <summary>Where a fill that lost leaves the value that was stored, so the router keeps that one.</summary>
internal sealed class FillBox<T>
    where T : class
{
    /// <summary>The fill lost to what the key holds, whatever that is: a value, a cached null or unreadable bytes. A lost fill is never sent to the other path.</summary>
    public bool Lost { get; set; }

    /// <summary>The winner is a cached null, which is the answer for the key.</summary>
    public bool CachedNull { get; set; }

    public T? Stored { get; set; }

    /// <summary>The key holds bytes the cache cannot read.</summary>
    public bool Unreadable { get; set; }

    /// <summary>When the stored value expires, as the winner's own read reported it; null for none.</summary>
    public DateTimeOffset? StoredExpiration { get; set; }
}
