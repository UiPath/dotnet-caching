namespace UiPath.Caching;

/// <summary>What <see cref="ICache.TryFillAsync{T}(CacheKey, T, TimeSpan, CachePolicy, CancellationToken)"/> did.</summary>
public enum FillOutcome
{
    /// <summary>The store could not answer or the value could not be stored; <c>default</c>, so an unconfigured mock never reads as a success.</summary>
    Failed = 0,

    Added,

    /// <summary>The key held a value, which is <see cref="FillResult{T}.Existing"/>; nothing was written.</summary>
    Existed,

    /// <summary>The key holds a value no registered serializer reads; nothing was written.</summary>
    Unreadable,

    /// <summary>The key was removed after the generator of the value started, so nothing was written; only a fill that follows a generator reports it, and only with a remove tombstone configured.</summary>
    Removed,
}
