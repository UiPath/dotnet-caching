namespace UiPath.Caching;

/// <summary>What <see cref="ICache.SetIfChangedAsync{T}(CacheKey, T, IEqualityComparer{T}, TimeSpan, CachePolicy, CancellationToken)"/> did.</summary>
public enum ChangeOutcome
{
    /// <summary>The store could not answer or the value could not be stored; <c>default</c>, so an unconfigured mock never reads as a success.</summary>
    Failed = 0,

    /// <summary>The key was absent; written without an event.</summary>
    Filled,

    /// <summary>An equal value was held; nothing was written, lifetime unchanged.</summary>
    Unchanged,

    /// <summary>Replaced and announced as a write is.</summary>
    Replaced,
}
