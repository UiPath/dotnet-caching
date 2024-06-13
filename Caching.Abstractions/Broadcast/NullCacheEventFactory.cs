namespace UiPath.Platform.Caching.Broadcast;

[ExcludeFromCodeCoverage]
public sealed class NullCacheEventFactory : ICacheEventFactory
{
    private static ICacheEvent NullEvent = new NullCacheEvent();

    public static NullCacheEventFactory Instance { get; } = new NullCacheEventFactory();

    private NullCacheEventFactory()
    {
    }

    public ICacheEvent Create(string cacheName, string eventType, CacheEventData eventData, string? id = null) =>
        NullEvent;

    public bool IsKnown(string? eventType) =>
        true;

    private sealed class NullCacheEvent : ICacheEvent
    {
        public CacheEventData? Data => null;

        public string? Id => null;

        public string? Type => null;

        public Uri? Source => null;

        public bool IsValid() => true;
    }
}
