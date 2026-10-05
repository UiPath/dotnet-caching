namespace UiPath.Caching;

/// <summary>An <see cref="IHashCache"/> that reads by the key's text without building a <see cref="CacheKey"/>; <see cref="SpanKeyExtensions"/> prefers it.</summary>
public interface ISpanKeyHashCache
{
    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetItemAsync<T>(Span<char> cacheKey, string field, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default);
}
