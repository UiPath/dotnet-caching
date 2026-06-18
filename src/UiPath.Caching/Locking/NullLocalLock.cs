namespace UiPath.Caching.Locking;

[ExcludeFromCodeCoverage]
public sealed class NullLocalLock : ILocalLock
{
    public static readonly NullLocalLock Instance = new();
    private static readonly NullDisposable s_disposable = new();

    public ValueTask<IDisposable> AcquireAsync(string key, CancellationToken token) =>
        new(s_disposable);

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
