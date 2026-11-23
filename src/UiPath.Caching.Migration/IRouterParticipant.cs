using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Migration;

/// <summary>What the router and its core need from a cache whatever its value type.</summary>
internal interface IRouterParticipant
{
    /// <summary>Parts taken but not yet in use: <see cref="Commit"/> puts them to work, disposing first gives them back.</summary>
    internal interface IAttachment : IDisposable
    {
        void Commit();
    }

    bool IsSubscribed { get; }

    /// <summary>The cache's topic once the library is attached.</summary>
    TopicKey? Topic { get; }

    Task WhenPendingSettledAsync();

    /// <summary>Drops this node's local copies and joins to loads that began before.</summary>
    void ResetLocal();

    /// <summary>The final eviction and announcement of every pending key, and one pass over the invalidations that could not be published.</summary>
    Task FlushAsync();

    /// <summary>Clears this node's copies of the cache and announces a cache-level invalidation, for what a bounded stop could not settle.</summary>
    Task InvalidateAsync();

    /// <summary>Takes the library parts without using them yet.</summary>
    IAttachment Prepare(LibraryParts library);
}
