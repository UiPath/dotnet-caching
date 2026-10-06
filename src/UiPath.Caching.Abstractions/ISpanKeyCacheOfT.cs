namespace UiPath.Caching;

/// <summary>An <see cref="ICache{T}"/> that reads by the key's text without building a <see cref="CacheKey"/>; <see cref="SpanKeyExtensions"/> prefers it.</summary>
public interface ISpanKeyCache<T>
{
    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetAsync(Span<char> cacheKey, CancellationToken token = default);
}
