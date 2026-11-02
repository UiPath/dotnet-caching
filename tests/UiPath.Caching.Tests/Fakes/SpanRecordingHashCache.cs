using System.Reflection;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>An <see cref="IHashCache"/> with the span capability that records the member and the text of each key it is handed there; its <see cref="CacheKey"/> members throw.</summary>
internal sealed class SpanRecordingHashCache : IHashCache, ISpanKeyHashCache
{
    public List<(string Tag, string Key)> Calls { get; } = [];

    public string Name => "recording";

    public void Dispose()
    {
    }

    public ValueTask<T?> GetItemAsync<T>(Span<char> cacheKey, string field, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(Span<char> cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<bool> ContainsAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        Calls.Add((Shape(MethodBase.GetCurrentMethod()!), cacheKey.ToString()));
        return default;
    }

    public ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) => throw new NotSupportedException();

    internal static string Shape(MethodBase method) =>
        $"{method.Name}({string.Join(',', method.GetParameters().Where(p => p.Name is not ("cacheKey" or "token")).Select(p => p.Name switch { "generator" => "gen", "expiration" => p.ParameterType.Name, _ => p.Name! }))})";
}
