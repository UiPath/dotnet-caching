namespace UiPath.Caching.Migration;

internal sealed class LibraryBatch<T>(ICache cache, Func<string, string> redisKey, Func<WriteLease>? writePolicy, Func<CachePolicy?>? readPolicy) : ILibraryBatch<T>
    where T : class
{
    public async ValueTask<KeyValuePair<string, T?>[]> GetAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var read = await cache.GetAsync<T>(Verbatim(keys), readPolicy?.Invoke(), cancellationToken).ConfigureAwait(false);
        var result = new KeyValuePair<string, T?>[keys.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new KeyValuePair<string, T?>(keys[i], i < read.Length ? read[i].Value : null);
        }

        return result;
    }

    public async ValueTask<bool> SetAsync(IReadOnlyList<KeyValuePair<string, T?>> entries, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        var pairs = new KeyValuePair<CacheKey, T?>[entries.Count];
        for (var i = 0; i < pairs.Length; i++)
        {
            pairs[i] = new KeyValuePair<CacheKey, T?>(CacheKey.Verbatim(redisKey(entries[i].Key)), entries[i].Value);
        }

        using var lease = writePolicy?.Invoke();
        return await cache.SetAsync(pairs, deadline ?? DateTimeOffset.MaxValue, lease?.Policy, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> RemoveAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) => cache.RemoveAsync<T>(Verbatim(keys), cancellationToken);

    public async ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState>(KeyValuePair<string, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CacheExpiry expiry, CancellationToken cancellationToken)
        where TState : notnull
    {
        var keyed = new KeyValuePair<CacheKey, TState>[entries.Length];
        for (var i = 0; i < keyed.Length; i++)
        {
            keyed[i] = new KeyValuePair<CacheKey, TState>(CacheKey.Verbatim(redisKey(entries[i].Key)), entries[i].Value);
        }

        return expiry switch
        {
            { Deadline: { } at } => await cache.GetOrAddAsync<T, TState>(keyed, generator, at, null, cancellationToken).ConfigureAwait(false),
            { Relative: { } span } => await cache.GetOrAddAsync<T, TState>(keyed, generator, span, null, cancellationToken).ConfigureAwait(false),
            _ => await cache.GetOrAddAsync<T, TState>(keyed, generator, (CachePolicy?)null, cancellationToken).ConfigureAwait(false),
        };
    }

    // The key text is made once per key and used for the call, whatever the caller's keys repeat.
    private CacheKey[] Verbatim(IReadOnlyList<string> keys)
    {
        var made = new CacheKey[keys.Count];
        for (var i = 0; i < made.Length; i++)
        {
            made[i] = CacheKey.Verbatim(redisKey(keys[i]));
        }

        return made;
    }
}
