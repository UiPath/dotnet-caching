using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Broadcast;

public sealed class ChangeTokenFactory<T> : IChangeTokenFactory
{
#pragma warning disable IDE1006 // Naming Styles
    private readonly ISet<string> MemoryAcceptedEvents = new HashSet<string>([KnownEventTypes.CacheRemoved, KnownEventTypes.CacheRefreshed], StringComparer.InvariantCultureIgnoreCase);
    private readonly ISerializerProxy<T> _serializer;
#pragma warning restore IDE1006 // Naming Styles

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ChangeTokenFactory<T>> _logger;
    private readonly Uri? _sourceUri;
    private readonly ICachingTelemetryProvider _telemetryProvider;

    public ChangeTokenFactory(IOptions<CacheOptions> optionsAccessor, ISerializerProxy<T> serializer, ILoggerFactory loggerFactory, ICachingTelemetryProvider telemetryProvider)
    {
        _sourceUri = optionsAccessor.Value.SourceUri;
        _serializer = serializer;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ChangeTokenFactory<T>>();
        _telemetryProvider = telemetryProvider;
    }

    public ICacheChangeToken Create(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryType)
    {
        _logger.LogTrace("Create change token. topic {TopicKey} token {Token} source {SourceUri}", topic.TopicKey, token, _sourceUri);
        var acceptedEvents = KnownCacheProviderNames.InMemory.Equals(cacheName, StringComparison.OrdinalIgnoreCase) ? MemoryAcceptedEvents : null;
        return new ChangeToken<T>(token, topic, _sourceUri, _serializer, _loggerFactory.CreateLogger<ChangeToken<T>>(), _telemetryProvider, acceptedEvents);
    }
}
