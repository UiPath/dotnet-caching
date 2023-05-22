namespace UiPath.Platform.Caching.Broadcast;

public sealed class ChangeTokenFactory : IChangeTokenFactory
{
    private readonly IChannelSubscriber<ICacheEvent> _subscriber;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ChangeTokenFactory> _logger;
    private readonly Uri? _sourceUri;

    public ChangeTokenFactory(IOptions<BroadcastOptions> broadcastOptionsAccessor, IChannelSubscriber<ICacheEvent> subscriber, ILoggerFactory loggerFactory)
    {
        _sourceUri = broadcastOptionsAccessor.Value.SourceUri;
        _subscriber = subscriber;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ChangeTokenFactory>();
    }

    public IChangeToken Create(Channel channel, string token)
    {
        _logger.LogTrace("Create change token. channel {} token {} source {}", channel, token, _sourceUri);
        return new ChangeToken(token, channel, _subscriber, _sourceUri, _loggerFactory.CreateLogger<ChangeToken>());
    }
}
