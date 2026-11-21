namespace UiPath.Caching.Migration;

/// <summary>One of the two clients of a cache, as the router sees both: the application source, or the library.</summary>
internal interface IPathClient<T>
    where T : class
{
    /// <summary>How long an unknown write can stay in flight, when the client knows.</summary>
    TimeSpan? SettleWithin { get; }

    ValueTask<PathRead<T>> ReadAsync(string key, CancellationToken cancellationToken);

    /// <param name="deadline">Null means no expiry.</param>
    ValueTask<WriteResult> SetAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken);

    /// <param name="deadline">Null means no expiry.</param>
    ValueTask<AddResult> TryAddAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken);

    ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken);
}
