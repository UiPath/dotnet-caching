using Microsoft.Extensions.Primitives;

namespace UiPath.Platform.Caching.Tests.Memory;

public class TestChangeTokenFactory : IChangeTokenFactory
{
    private readonly Func<Channel, string, IChangeToken?> _func;

    public TestChangeTokenFactory(Func<Channel, string, IChangeToken?> func)
    {
        _func = func;
    }
    public IChangeToken? Create(Channel channel, string key)
    {
        return _func(channel, key);
    }

}
