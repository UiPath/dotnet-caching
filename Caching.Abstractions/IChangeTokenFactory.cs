

using Microsoft.Extensions.Primitives;

namespace UiPath.Platform.Caching;

public interface IChangeTokenFactory
{
    public IChangeToken Create(Channel channel, string token, Uri? source = null);
}
