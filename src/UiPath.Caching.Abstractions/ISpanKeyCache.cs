namespace UiPath.Caching;

/// <summary>An <see cref="ICache"/> that reads by the key's text without building a <see cref="CacheKey"/>; <see cref="SpanKeyExtensions"/> prefers it.</summary>
/// <remarks>Apart from <see cref="ICache"/>, so a mock of <see cref="ICache"/> never has to proxy a <c>Span&lt;char&gt;</c> parameter.</remarks>
public interface ISpanKeyCache
{
    /// <summary>Reads by the key's text, normalized as <c>new CacheKey(text)</c> normalizes it, so a local hit need not allocate.</summary>
    ValueTask<T?> GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default);
}
