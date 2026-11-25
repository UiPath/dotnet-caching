namespace UiPath.Caching.Migration;

// The span-key members: each builds the key from its text and goes to the member of the same name that takes a CacheKey.
internal sealed partial class RoutedHashCache<T>
{
    private IHashCache<T> Surface => this;

    ValueTask<T?> ISpanKeyHashCache<T>.GetItemAsync(Span<char> cacheKey, string field, CancellationToken token) => Surface.GetItemAsync(new CacheKey(cacheKey), field, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache<T>.GetAsync(Span<char> cacheKey, CancellationToken token) => Surface.GetAsync(new CacheKey(cacheKey), token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache<T>.GetAsync(Span<char> cacheKey, string[] fields, CancellationToken token) => Surface.GetAsync(new CacheKey(cacheKey), fields, token);

    ValueTask<ICacheEntry<IDictionary<string, T?>>> ISpanKeyHashCache<T>.GetCacheEntryAsync(Span<char> cacheKey, CancellationToken token) => Surface.GetCacheEntryAsync(new CacheKey(cacheKey), token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache<T>.GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token) => Surface.GetOrAddAsync(new CacheKey(cacheKey), generator, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache<T>.GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token) => Surface.GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache<T>.GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token) => Surface.GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache<T>.GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token) => Surface.GetOrAddAsync(new CacheKey(cacheKey), state, generator, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache<T>.GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token) => Surface.GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache<T>.GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token) => Surface.GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, token);

    ValueTask<bool> ISpanKeyHashCache<T>.ContainsAsync(Span<char> cacheKey, CancellationToken token) => Surface.ContainsAsync(new CacheKey(cacheKey), token);

    ValueTask<TimeSpan?> ISpanKeyHashCache<T>.TimeToLiveAsync(Span<char> cacheKey, CancellationToken token) => Surface.TimeToLiveAsync(new CacheKey(cacheKey), token);

    ValueTask<DateTimeOffset?> ISpanKeyHashCache<T>.ExpireTimeAsync(Span<char> cacheKey, CancellationToken token) => Surface.ExpireTimeAsync(new CacheKey(cacheKey), token);

    ValueTask<IDictionary<string, string?>?> ISpanKeyHashCache<T>.GetMetadataAsync(Span<char> cacheKey, CancellationToken token) => Surface.GetMetadataAsync(new CacheKey(cacheKey), token);

    ValueTask<bool> ISpanKeyHashCache<T>.SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, CancellationToken token) => Surface.SetAsync(new CacheKey(cacheKey), values, token);

    ValueTask<bool> ISpanKeyHashCache<T>.SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CancellationToken token) => Surface.SetAsync(new CacheKey(cacheKey), values, expiration, token);

    ValueTask<bool> ISpanKeyHashCache<T>.SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CancellationToken token) => Surface.SetAsync(new CacheKey(cacheKey), values, expiration, token);

    ValueTask<bool> ISpanKeyHashCache<T>.SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token) => Surface.SetAsync(new CacheKey(cacheKey), values, options, token);
}
