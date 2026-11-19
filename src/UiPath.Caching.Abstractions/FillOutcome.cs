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
}
