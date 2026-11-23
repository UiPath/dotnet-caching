namespace UiPath.Caching.Migration;

/// <summary>The batch calls of the library's own cache, used when no key move applies.</summary>
internal interface ILibraryBatch<T>
    where T : class
{
    /// <summary>One pair per input key, in the order of the input, duplicates included.</summary>
    ValueTask<KeyValuePair<string, T?>[]> GetAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken);

    ValueTask<bool> SetAsync(IReadOnlyList<KeyValuePair<string, T?>> entries, DateTimeOffset? deadline, CancellationToken cancellationToken);

    ValueTask<bool> RemoveAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken);

    /// <param name="expiry">Relative or absolute as the caller gave it: the library takes the life when it stores what the generator made.</param>
    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState>(KeyValuePair<string, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CacheExpiry expiry, CancellationToken cancellationToken)
        where TState : notnull;
}
