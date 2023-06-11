namespace UiPath.Platform.Caching.Tests;

public class TestChangeTokenFactory : IChangeTokenFactory
{
    private readonly Func<string, ITopic<ICacheEvent>, ICacheChangeToken> _func;

    public TestChangeTokenFactory(Func<string, ITopic<ICacheEvent>, ICacheChangeToken> func)
    {
        _func = func;
    }

    public ICacheChangeToken Create(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryTypec)
    {
        return _func(token, topic);
    }
}
