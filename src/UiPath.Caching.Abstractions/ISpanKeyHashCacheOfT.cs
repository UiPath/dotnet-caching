namespace UiPath.Caching;

/// <summary>An <see cref="IHashCache{T}"/> that reads by the key's text without building a <see cref="CacheKey"/>; <see cref="SpanKeyExtensions"/> prefers it.</summary>
public interface ISpanKeyHashCache<T>
{
    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetItemAsync(Span<char> cacheKey, string field, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetAsync(Span<char> cacheKey, CancellationToken token = default);
}
