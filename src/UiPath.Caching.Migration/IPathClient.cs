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

    /// <summary>The library's own batch, when the client is the plain library path.</summary>
    ILibraryBatch<T>? PlainBatch => null;

    /// <summary>The group's keys are moving: the client runs key move forms.</summary>
    bool Moving => false;

    ValueTask<PathRead<T>> ReadAsync(string key, CancellationToken cancellationToken);

    /// <param name="deadline">Null means no expiry.</param>
    ValueTask<WriteResult> SetAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken);

    /// <param name="deadline">Null means no expiry.</param>
    ValueTask<AddResult> TryCreateAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken);

    ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken);

    /// <summary>A miss fill: written only when absent, never over a value; a lost fill leaves the winner in the box.</summary>
    ValueTask<WriteResult> FillAsync(string key, T value, DateTimeOffset? deadline, FillBox<T>? stored, CancellationToken cancellationToken);

    /// <summary>Completes when the client's retries for the key return.</summary>
    Task SettledAsync(string key) => PathClientTasks.NoPendingRetry;

    /// <summary>Stops scheduling retries; the task ends when the running attempts return.</summary>
    Task StopRetriesAsync() => Task.CompletedTask;

    /// <summary>A conditional create; a client with no such form refuses it.</summary>
    ValueTask<TryAddResult<T>> TryAddAsync(string key, T? value, DateTimeOffset? deadline, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This client has no conditional create.");

    /// <summary>The library's atomic swap; a client with none refuses it, and a failure after the send is Unknown.</summary>
    ValueTask<SwapResult<T>> SetIfChangedAsync(string key, T? value, IEqualityComparer<T> comparer, DateTimeOffset? deadline, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This client has no compare-and-swap.");

    /// <summary>The library's own fill, which writes only when absent and announces nothing; a client with no such form refuses it.</summary>
    ValueTask<FillResult<T>> TryFillAsync(string key, T? value, DateTimeOffset? deadline, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This client has no fill.");

    /// <summary>Whether the key is present, a stored null included; null when the client has no such form and the read answers.</summary>
    ValueTask<bool?> ContainsAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<bool?>(null);

    /// <summary>The remaining life of the key as the client reports it; null for an absent key and for one with no expiry.</summary>
    ValueTask<TimeSpan?> TimeToLiveAsync(string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This client reports no remaining life.");

    /// <summary>The time the key expires; null for an absent key and for one with no expiry.</summary>
    ValueTask<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This client reports no expiry.");

    /// <summary>Extends a key's life: Committed, NotSent when absent, Unknown when it may have; a client with none refuses it.</summary>
    ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This client has no refresh.");

    /// <summary>A local event for the key: whatever the client remembers about it is dropped.</summary>
    void KeyChanged(string key)
    {
    }

    /// <summary>A gap in the events: nothing the client remembers is kept.</summary>
    void Gap()
    {
    }
}
