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

    private static TResult Complete<TResult>(ValueTask<TResult> read) =>
        read.IsCompletedSuccessfully ? read.Result : read.AsTask().GetAwaiter().GetResult();
}
