namespace UiPath.Platform.Caching.Locking;

[ExcludeFromCodeCoverage]
public sealed class NullDistributedLock : IDistributedLock
{
    public static readonly NullDistributedLock Instance = new();

    public ValueTask<IAsyncDisposable> AcquireAsync(string key, TimeSpan expiry, TimeSpan waitTimeout, CancellationToken token) =>
        new(NoOpAsyncDisposable.Instance);

    public ValueTask<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan expiry, CancellationToken token) =>
        new(NoOpAsyncDisposable.Instance);
}
