namespace UiPath.Caching.Tests.Fakes;

/// <summary>Holds a local write inside <c>MemoryCacheSetter.Set</c>, before it commits, until the test lets it go.</summary>
internal sealed class GatedChangeTokenFactory : IChangeTokenFactory
{
    private readonly ManualResetEventSlim _entered = new();
    private readonly ManualResetEventSlim _release = new();

    public bool WaitUntilEntered(TimeSpan timeout, CancellationToken token) => _entered.Wait(timeout, token);

    public void Release() => _release.Set();

    public ICacheChangeToken Create(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryType)
    {
        _entered.Set();
        _release.Wait(TimeSpan.FromSeconds(30));
        return NullCacheChangeToken.Instance;
    }
}
