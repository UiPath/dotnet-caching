namespace UiPath.Caching.Migration;

/// <summary>Where a fill that lost leaves the value that was stored, so the router keeps that one.</summary>
internal sealed class FillBox<T>
    where T : class
{
    public T? Stored { get; set; }

    /// <summary>When the stored value expires, as the winner's own read reported it; null for none.</summary>
    public DateTimeOffset? StoredExpiration { get; set; }
}
