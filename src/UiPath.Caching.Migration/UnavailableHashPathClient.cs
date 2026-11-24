namespace UiPath.Caching.Migration;

/// <summary>The library path of a hash cache before the library has started: every read is unavailable and no write is sent.</summary>
internal sealed class UnavailableHashPathClient<T> : IHashPathClient<T>
    where T : class
{
    public static readonly UnavailableHashPathClient<T> Instance = new();

    public TimeSpan? SettleWithin => null;

    public ValueTask<HashRead<T>> ReadAsync(string key, string[]? fields, CancellationToken cancellationToken) => ValueTask.FromResult(HashRead<T>.Of(ReadOutcome.Unavailable));

    public ValueTask<bool?> ContainsAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<bool?>(null);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken cancellationToken) => throw Unavailable("an expiry");

    public ValueTask<WriteResult> SetAsync(string key, IDictionary<string, T?> fields, HashWriteOptions options, CancellationToken cancellationToken) => NotSent();

    public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken) => NotSent();

    public ValueTask<WriteResult> RemoveFieldsAsync(string key, string[] fields, CancellationToken cancellationToken) => NotSent();

    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, CancellationToken cancellationToken) => NotSent();

    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, IDictionary<string, string?>? metadata, CancellationToken cancellationToken) => NotSent();

    public ValueTask<WriteResult> FillAsync(string key, IDictionary<string, T?> fields, HashWriteOptions options, HashFillBox<T>? stored, CancellationToken cancellationToken) => NotSent();

    private static ValueTask<WriteResult> NotSent() => ValueTask.FromResult<WriteResult>(WriteOutcome.NotSent);

    private static CacheUnavailableException Unavailable(string what) => new($"The library has not started, so {what} cannot be answered.");
}
