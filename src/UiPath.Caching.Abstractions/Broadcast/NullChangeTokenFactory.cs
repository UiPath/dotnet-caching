namespace UiPath.Caching.Broadcast;

[ExcludeFromCodeCoverage]
public sealed class NullChangeTokenFactory : IChangeTokenFactory
{
    public static readonly NullChangeTokenFactory Instance = new NullChangeTokenFactory();

    public ICacheChangeToken Create(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryType)
        => NullCacheChangeToken.Instance;
}
