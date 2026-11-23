namespace UiPath.Caching.Migration;

/// <summary>The library path before the library has started: every read is unavailable and no write is sent.</summary>
internal sealed class UnavailablePathClient<T> : IPathClient<T>
    where T : class
{
    public static readonly UnavailablePathClient<T> Instance = new();

    public TimeSpan? SettleWithin => null;

    public ValueTask<PathRead<T>> ReadAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult(PathRead<T>.Of(ReadOutcome.Unavailable));

    public ValueTask<WriteResult> SetAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken) => ValueTask.FromResult<WriteResult>(WriteOutcome.NotSent);

    public ValueTask<AddResult> TryAddAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken) => ValueTask.FromResult<AddResult>(AddOutcome.NotSent);

    public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<WriteResult>(WriteOutcome.NotSent);
}
