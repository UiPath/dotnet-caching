namespace UiPath.Caching.Migration.Tests.Support;

internal sealed class TestEntry<T>(T? value, DateTimeOffset expiration) : ICacheEntry<T?>
{
    public DateTimeOffset Expiration { get; } = expiration;

    public IDictionary<string, string?>? Metadata => null;

    public T? Value { get; } = value;

    object? ICacheEntry.Value => Value;

    public bool Found => Expiration > DateTimeOffset.MinValue;

    public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null) =>
        new TestEntry<T>(Value, expiration ?? Expiration);
}

#pragma warning disable CS0067
internal sealed class FakeConnection : IConnectionState
{
    public event EventHandler? OnConnectionFailed;

    public event EventHandler? OnConnectionRestored;

    public event EventHandler? OnReconnected;

    public bool IsConnected { get; set; } = true;
}
#pragma warning restore CS0067

/// <summary>The library cache as a script: what a snapshot read does, and what a write answers.</summary>
internal sealed class FakeLibraryStore : ILibraryStore
{
    public Func<CacheKey, CancellationToken, Task<object?>> Read { get; set; } = (_, _) => Task.FromResult<object?>(null);

    public Func<CacheKey, CancellationToken, Task<bool>> Write { get; set; } = (_, _) => Task.FromResult(true);

    public List<CacheKey> Keys { get; } = [];

    public async ValueTask<ICacheEntry<T?>> ReadSnapshotAsync<T>(CacheKey key, CancellationToken cancellationToken)
    {
        Keys.Add(key);
        var value = await Read(key, cancellationToken);
        return value is T typed
            ? new TestEntry<T>(typed, DateTimeOffset.UtcNow.AddMinutes(1))
            : new TestEntry<T>(default, DateTimeOffset.MinValue);
    }

    public async ValueTask<bool> SetAsync<T>(CacheKey key, T value, DateTimeOffset expiration, CancellationToken cancellationToken)
    {
        Keys.Add(key);
        return await Write(key, cancellationToken);
    }

    public async ValueTask<bool> RemoveAsync<T>(CacheKey key, CancellationToken cancellationToken)
    {
        Keys.Add(key);
        return await Write(key, cancellationToken);
    }
}
