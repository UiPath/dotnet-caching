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

/// <summary>The library cache as a script: a read answers a value, a <see cref="SnapshotOutcome"/> or nothing (absent), and a write answers a <see cref="StoreOutcome"/>.</summary>
internal sealed class FakeMigrationCache : IMigrationCache
{
    public Func<CacheKey, CancellationToken, Task<object?>> Read { get; set; } = (_, _) => Task.FromResult<object?>(null);

    public Func<CacheKey, CancellationToken, Task<StoreOutcome>> Write { get; set; } = (_, _) => Task.FromResult(StoreOutcome.Committed);

    public List<CacheKey> Keys { get; } = [];

    /// <summary>The policy each read was given, in order.</summary>
    public List<CachePolicy?> ReadPolicies { get; } = [];

    /// <summary>The policy each write was given, in order.</summary>
    public List<CachePolicy?> WritePolicies { get; } = [];

    public async ValueTask<SnapshotRead<T>> ReadSnapshotAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        Keys.Add(cacheKey);
        ReadPolicies.Add(policy);
        return await Read(cacheKey, token) switch
        {
            T typed => new SnapshotRead<T>(SnapshotOutcome.Found, new TestEntry<T>(typed, DateTimeOffset.UtcNow.AddMinutes(1))),
            SnapshotOutcome outcome => new SnapshotRead<T>(outcome, null),
            _ => new SnapshotRead<T>(SnapshotOutcome.Absent, null),
        };
    }

    public async ValueTask<StoreOutcome> WriteAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        Keys.Add(cacheKey);
        WritePolicies.Add(policy);
        return await Write(cacheKey, token);
    }

    public async ValueTask<StoreOutcome> DeleteAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        Keys.Add(cacheKey);
        return await Write(cacheKey, token);
    }
}