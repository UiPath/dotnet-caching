namespace UiPath.Caching.Migration;

/// <summary>One of the two clients of a hash cache, as the router sees both: the application source, or the library.</summary>
internal interface IHashPathClient<T>
    where T : class
{
    /// <summary>How long an unknown write can stay in flight, when the client knows.</summary>
    TimeSpan? SettleWithin { get; }

    /// <summary>The client has no other client to fall back on while its rules apply: its failures are the call's answer.</summary>
    bool NoFallback => false;

    /// <summary>The client keeps the hash a lost fill leaves behind in the box.</summary>
    bool KeepsStoredOnFill => false;

    /// <summary>The keys that have an attempt still outstanding: waiting, running, or superseded while it ran.</summary>
    IReadOnlyCollection<string> InFlightKeys => [];

    /// <summary>The group's keys are moving: the client runs key move forms.</summary>
    bool Moving => false;

    /// <param name="fields">The fields to return, or null for the whole hash.</param>
    ValueTask<HashRead<T>> ReadAsync(string key, string[]? fields, CancellationToken cancellationToken);

    /// <summary>Whether the key is present; null when the client cannot say.</summary>
    ValueTask<bool?> ContainsAsync(string key, CancellationToken cancellationToken);

    /// <summary>The time the key expires; null for an absent key and for one with no expiry. No promotion is run for it.</summary>
    ValueTask<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken cancellationToken);

    ValueTask<WriteResult> SetAsync(string key, IDictionary<string, T?> fields, HashWriteOptions options, CancellationToken cancellationToken);

    ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken);

    ValueTask<WriteResult> RemoveFieldsAsync(string key, string[] fields, CancellationToken cancellationToken);

    /// <summary>The refresh that sets the expiry only: Committed, NotSent when the hash is absent, Unknown when it may have applied. The metadata the hash holds stays.</summary>
    ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, CancellationToken cancellationToken);

    /// <summary>The refresh with options, as the library's options overload: the metadata replaces the one the hash holds, and null clears it. Refused with <see cref="NotSupportedException"/> where the client has no checked form for the metadata.</summary>
    ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, IDictionary<string, string?>? metadata, CancellationToken cancellationToken);

    /// <summary>A whole-hash miss fill: written only when absent, never over a hash; a lost fill leaves the winner in the box.</summary>
    ValueTask<WriteResult> FillAsync(string key, IDictionary<string, T?> fields, HashWriteOptions options, HashFillBox<T>? stored, CancellationToken cancellationToken);

    /// <summary>Completes when the client has no retry of the key left; <see cref="PathClientTasks.NoPendingRetry"/> when it has none.</summary>
    Task SettledAsync(string key) => PathClientTasks.NoPendingRetry;

    /// <summary>Stops scheduling retries; the task ends when the running attempts return.</summary>
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
