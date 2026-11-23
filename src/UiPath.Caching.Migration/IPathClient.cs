namespace UiPath.Caching.Migration;

/// <summary>One of the two clients of a cache, as the router sees both: the application source, or the library.</summary>
internal interface IPathClient<T>
    where T : class
{
    /// <summary>How long an unknown write can stay in flight, when the client knows.</summary>
    TimeSpan? SettleWithin { get; }

    /// <summary>The client has no other client to fall back on while its rules apply: its failures are the call's answer.</summary>
    bool NoFallback => false;

    /// <summary>The client keeps the value a lost fill leaves behind in the box.</summary>
    bool KeepsStoredOnFill => false;

    /// <summary>The keys that have an attempt still outstanding: waiting, running, or superseded while it ran.</summary>
    IReadOnlyCollection<string> InFlightKeys => [];

    ValueTask<PathRead<T>> ReadAsync(string key, CancellationToken cancellationToken);

    /// <param name="deadline">Null means no expiry.</param>
    ValueTask<WriteResult> SetAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken);

    /// <param name="deadline">Null means no expiry.</param>
    ValueTask<AddResult> TryAddAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken);

    ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken);

    /// <summary>A miss fill: an NX write that never clobbers or erases; the plain write where the client has no such form.</summary>
    ValueTask<WriteResult> FillAsync(string key, T value, DateTimeOffset? deadline, FillBox<T>? stored, CancellationToken cancellationToken) => SetAsync(key, value, deadline, cancellationToken);

    /// <summary>Completes when the client's retries for the key return.</summary>
    Task SettledAsync(string key) => PathClientTasks.NoPendingRetry;

    /// <summary>No retry is scheduled any more: what is waiting is dropped; the task ends when the attempts that were running have returned.</summary>
    Task StopRetriesAsync() => Task.CompletedTask;

    /// <summary>A local event for the key: whatever the client remembers about it is dropped.</summary>
    void KeyChanged(string key)
    {
    }

    /// <summary>A gap in the events: nothing the client remembers is kept.</summary>
    void Gap()
    {
    }
}
