namespace UiPath.Caching.Tests.Fakes;

/// <summary>Span reads from a test's string key, on the stack, so the read itself is what gets measured.</summary>
internal static class SpanReads
{
    public static T? Read<T>(ICache cache, string key, CancellationToken token = default)
    {
        return Complete(cache.GetAsync<T>($"{key}", policy: null, token));
    }

    public static T? Read<T>(ICache<T> cache, string key, CancellationToken token = default)
    {
        return Complete(cache.GetAsync($"{key}", token));
    }

    public static T? ReadItem<T>(IHashCache cache, string key, string field, CancellationToken token = default)
    {
        return Complete(cache.GetItemAsync<T>($"{key}", field, policy: null, token));
    }

    public static T? ReadItem<T>(IHashCache<T> cache, string key, string field, CancellationToken token = default)
    {
        return Complete(cache.GetItemAsync($"{key}", field, token));
    }

    public static IDictionary<string, T?> ReadAll<T>(IHashCache cache, string key, CancellationToken token = default)
    {
        return Complete(cache.GetAsync<T>($"{key}", policy: null, token));
    }

    public static IDictionary<string, T?> ReadAll<T>(IHashCache<T> cache, string key, CancellationToken token = default)
    {
        return Complete(cache.GetAsync($"{key}", token));
    }

    public static bool Contains<T>(ICache cache, string key, CancellationToken token = default)
    {
        return Complete(cache.ContainsAsync<T>($"{key}", token));
    }

    public static bool Contains<T>(ICache<T> cache, string key, CancellationToken token = default)
    {
        return Complete(cache.ContainsAsync($"{key}", token));
    }

    public static bool Contains<T>(IHashCache cache, string key, CancellationToken token = default)
    {
        return Complete(cache.ContainsAsync<T>($"{key}", token));
    }

    public static bool Contains<T>(IHashCache<T> cache, string key, CancellationToken token = default)
    {
        return Complete(cache.ContainsAsync($"{key}", token));
    }

    public static ICacheEntry<T?> Entry<T>(ICache cache, string key, CancellationToken token = default)
    {
        return Complete(cache.GetCacheEntryAsync<T>($"{key}", policy: null, token));
    }

    public static ICacheEntry<IDictionary<string, T?>> Entry<T>(IHashCache cache, string key, CancellationToken token = default)
    {
        return Complete(cache.GetCacheEntryAsync<T>($"{key}", policy: null, token));
    }

    public static ICacheEntry<IDictionary<string, T?>> Entry<T>(IHashCache<T> cache, string key, CancellationToken token = default)
    {
        return Complete(cache.GetCacheEntryAsync($"{key}", token));
    }

    public static T? GetOrAdd<T>(ICache cache, string key, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy = null, CancellationToken token = default)
    {
        return Complete(cache.GetOrAddAsync($"{key}", generator, policy, token));
    }

    public static T? GetOrAdd<T>(ICache<T> cache, string key, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        return Complete(cache.GetOrAddAsync($"{key}", generator, token));
    }

    public static IDictionary<string, T?> GetOrAdd<T>(IHashCache cache, string key, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy = null, CancellationToken token = default)
    {
        return Complete(cache.GetOrAddAsync($"{key}", generator, policy, token));
    }

    public static IDictionary<string, T?> GetOrAdd<T>(IHashCache<T> cache, string key, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
    {
        return Complete(cache.GetOrAddAsync($"{key}", generator, token));
    }

    public static IDictionary<string, T?> ReadFields<T>(IHashCache cache, string key, string[] fields, CancellationToken token = default)
    {
        return Complete(cache.GetAsync<T>($"{key}", fields, policy: null, token));
    }

    public static IDictionary<string, T?> ReadFields<T>(IHashCache<T> cache, string key, string[] fields, CancellationToken token = default)
    {
        return Complete(cache.GetAsync($"{key}", fields, token));
    }

    public static TimeSpan? TimeToLive<T>(ICache cache, string key, CancellationToken token = default)
    {
        return Complete(cache.TimeToLiveAsync<T>($"{key}", token));
    }

    public static DateTimeOffset? ExpireTime<T>(ICache cache, string key, CancellationToken token = default)
    {
        return Complete(cache.ExpireTimeAsync<T>($"{key}", token));
    }

    public static TimeSpan? TimeToLive<T>(ICache<T> cache, string key, CancellationToken token = default)
    {
        return Complete(cache.TimeToLiveAsync($"{key}", token));
    }

    public static DateTimeOffset? ExpireTime<T>(ICache<T> cache, string key, CancellationToken token = default)
    {
        return Complete(cache.ExpireTimeAsync($"{key}", token));
    }

    public static TimeSpan? TimeToLive<T>(IHashCache cache, string key, CancellationToken token = default)
    {
        return Complete(cache.TimeToLiveAsync<T>($"{key}", token));
    }

    public static DateTimeOffset? ExpireTime<T>(IHashCache cache, string key, CancellationToken token = default)
    {
        return Complete(cache.ExpireTimeAsync<T>($"{key}", token));
    }

    public static TimeSpan? TimeToLive<T>(IHashCache<T> cache, string key, CancellationToken token = default)
    {
        return Complete(cache.TimeToLiveAsync($"{key}", token));
    }

    public static DateTimeOffset? ExpireTime<T>(IHashCache<T> cache, string key, CancellationToken token = default)
    {
        return Complete(cache.ExpireTimeAsync($"{key}", token));
    }

    public static IDictionary<string, string?>? Metadata<T>(IHashCache cache, string key, CancellationToken token = default)
    {
        return Complete(cache.GetMetadataAsync<T>($"{key}", token));
    }

    public static IDictionary<string, string?>? Metadata<T>(IHashCache<T> cache, string key, CancellationToken token = default)
    {
        return Complete(cache.GetMetadataAsync($"{key}", token));
    }

    public static TResult Complete<TResult>(ValueTask<TResult> read) =>
        read.IsCompletedSuccessfully ? read.Result : read.AsTask().GetAwaiter().GetResult();
}
