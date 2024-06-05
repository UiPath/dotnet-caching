namespace UiPath.Platform.Caching.Broadcast;

public sealed class ChangeTokenFactory : IChangeTokenFactory
{
#pragma warning disable IDE1006 // Naming Styles
    private static readonly ISet<string> MemoryAcceptedEvents = new HashSet<string>(new string[] { KnownEventTypes.CacheRemoved, KnownEventTypes.CacheRefreshed }, StringComparer.InvariantCultureIgnoreCase);
    private readonly ISerializerProxy _serializer;
#pragma warning restore IDE1006 // Naming Styles

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ChangeTokenFactory> _logger;
    private readonly Uri? _sourceUri;

    public ChangeTokenFactory(IOptions<CacheOptions> optionsAccessor, ISerializerProxy serializer, ILoggerFactory loggerFactory)
    {
        _sourceUri = optionsAccessor.Value.SourceUri;
        _serializer = serializer;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ChangeTokenFactory>();
    }

    public ICacheChangeToken Create(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryType)
    {
        _logger.LogTrace("Create change token. topic {TopicKey} token {Token} source {SourceUri}", topic.TopicKey, token, _sourceUri);
        var acceptedEvents = KnownCacheProviderNames.InMemory.Equals(cacheName, StringComparison.OrdinalIgnoreCase) ? MemoryAcceptedEvents : null;
        return new ChangeToken(token, topic, _sourceUri, _serializer, _loggerFactory.CreateLogger<ChangeToken>(), acceptedEvents);
    }
}
