namespace UiPath.Caching.Broadcast;

public interface IChangeTokenFactory
{
    ICacheChangeToken Create(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryType);
}
