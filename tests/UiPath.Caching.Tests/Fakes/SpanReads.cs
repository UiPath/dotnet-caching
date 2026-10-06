namespace UiPath.Caching.Tests.Fakes;

/// <summary>Span reads from a test's string key, on the stack, so the read itself is what gets measured.</summary>
internal static class SpanReads
{
    public static T? Read<T>(ICache cache, string key, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.GetAsync<T>(buffer[..key.Length], policy: null, token));
    }

    public static T? Read<T>(ICache<T> cache, string key, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.GetAsync(buffer[..key.Length], token));
    }

    public static T? ReadItem<T>(IHashCache cache, string key, string field, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.GetItemAsync<T>(buffer[..key.Length], field, policy: null, token));
    }

    public static T? ReadItem<T>(IHashCache<T> cache, string key, string field, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.GetItemAsync(buffer[..key.Length], field, token));
    }

    public static IDictionary<string, T?> ReadAll<T>(IHashCache cache, string key, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.GetAsync<T>(buffer[..key.Length], policy: null, token));
    }

    public static IDictionary<string, T?> ReadAll<T>(IHashCache<T> cache, string key, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.GetAsync(buffer[..key.Length], token));
    }

    public static bool Contains<T>(ICache cache, string key, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.ContainsAsync<T>(buffer[..key.Length], token));
    }

    public static bool Contains<T>(ICache<T> cache, string key, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.ContainsAsync(buffer[..key.Length], token));
    }

    public static bool Contains<T>(IHashCache cache, string key, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.ContainsAsync<T>(buffer[..key.Length], token));
    }

    public static bool Contains<T>(IHashCache<T> cache, string key, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.ContainsAsync(buffer[..key.Length], token));
    }

    public static ICacheEntry<T?> Entry<T>(ICache cache, string key, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.GetCacheEntryAsync<T>(buffer[..key.Length], policy: null, token));
    }

    public static ICacheEntry<IDictionary<string, T?>> Entry<T>(IHashCache cache, string key, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.GetCacheEntryAsync<T>(buffer[..key.Length], policy: null, token));
    }

    public static ICacheEntry<IDictionary<string, T?>> Entry<T>(IHashCache<T> cache, string key, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.GetCacheEntryAsync(buffer[..key.Length], token));
    }

    public static T? GetOrAdd<T>(ICache cache, string key, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy = null, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.GetOrAddAsync(buffer[..key.Length], generator, policy, token));
    }

    public static T? GetOrAdd<T>(ICache<T> cache, string key, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.GetOrAddAsync(buffer[..key.Length], generator, token));
    }

    public static IDictionary<string, T?> GetOrAdd<T>(IHashCache cache, string key, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy = null, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.GetOrAddAsync(buffer[..key.Length], generator, policy, token));
    }

    public static IDictionary<string, T?> GetOrAdd<T>(IHashCache<T> cache, string key, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
    {
        Span<char> buffer = stackalloc char[512];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.GetOrAddAsync(buffer[..key.Length], generator, token));
    }

    private static TResult Complete<TResult>(ValueTask<TResult> read) =>
        read.IsCompletedSuccessfully ? read.Result : read.AsTask().GetAwaiter().GetResult();
}
