namespace UiPath.Caching.Migration;

/// <summary>The library path before the library has started: every read is unavailable and no write is sent.</summary>
internal sealed class UnavailablePathClient<T> : IPathClient<T>
    where T : class
{
    public static readonly UnavailablePathClient<T> Instance = new();

    public TimeSpan? SettleWithin => null;

    public ValueTask<PathRead<T>> ReadAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult(PathRead<T>.Of(ReadOutcome.Unavailable));

    public ValueTask<WriteResult> SetAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken) => ValueTask.FromResult<WriteResult>(WriteOutcome.NotSent);

    public ValueTask<AddResult> TryCreateAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken) => ValueTask.FromResult<AddResult>(AddOutcome.NotSent);

    public ValueTask<WriteResult> FillAsync(string key, T value, DateTimeOffset? deadline, FillBox<T>? stored, CancellationToken cancellationToken) => ValueTask.FromResult<WriteResult>(WriteOutcome.NotSent);

    public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<WriteResult>(WriteOutcome.NotSent);

    public ValueTask<TryAddResult<T>> TryAddAsync(string key, T? value, DateTimeOffset? deadline, CancellationToken cancellationToken) => ValueTask.FromResult(TryAddResult<T>.Failed(WriteOutcome.NotSent));

    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, CancellationToken cancellationToken) => throw new CacheUnavailableException("The library has not started, so a refresh cannot be answered.");

    public ValueTask<SwapResult<T>> SetIfChangedAsync(string key, T? value, IEqualityComparer<T> comparer, DateTimeOffset? deadline, CancellationToken cancellationToken) => throw Unavailable("a compare-and-swap");

    public ValueTask<FillResult<T>> TryFillAsync(string key, T? value, DateTimeOffset? deadline, CancellationToken cancellationToken) => throw Unavailable("a fill");

    public ValueTask<TimeSpan?> TimeToLiveAsync(string key, CancellationToken cancellationToken) => throw Unavailable("a remaining life");

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken cancellationToken) => throw Unavailable("an expiry");

    private static CacheUnavailableException Unavailable(string what) => new($"The library has not started, so {what} cannot be answered.");
}
