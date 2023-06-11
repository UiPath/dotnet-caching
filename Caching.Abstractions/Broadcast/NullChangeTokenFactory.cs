using System.Diagnostics.CodeAnalysis;

namespace UiPath.Platform.Caching.Broadcast;

[ExcludeFromCodeCoverage]
public sealed class NullChangeTokenFactory : IChangeTokenFactory
{
    public static readonly IChangeTokenFactory Instance = new NullChangeTokenFactory();

    public ICacheChangeToken Create(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryType)
        => NullCacheChangeToken.Instance;
}
