namespace UiPath.Caching.Migration;

internal interface IStoppable
{
    Task WhenPendingSettledAsync();

    /// <summary>The final eviction and announcement of every pending key, and one pass over the invalidations that could not be published.</summary>
    Task FlushAsync();

    /// <summary>Clears this node's copies of the cache and announces a cache-level invalidation, for what a bounded stop could not settle.</summary>
    Task InvalidateAsync();
}
