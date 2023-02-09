using UiPath.Platform.Caching.Broadcast;

namespace UiPath.Platform.Caching.Hybrid;

public sealed class ChangeTokenFactory : IChangeTokenFactory
{
    private readonly IChannelSubscriber<IClearCacheEvent> _subscriber;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ChangeTokenFactory> _logger;

    public ChangeTokenFactory(IChannelSubscriber<IClearCacheEvent> subscriber, ILoggerFactory loggerFactory)
    {
        _subscriber = subscriber;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ChangeTokenFactory>();
    }

    public IChangeToken Create(Channel channel, string token, Uri? source = null)
    {
        _logger.LogTrace("Create change token. channel {} token {} source {}", channel, token, source);
        return new ChangeToken(token, channel, _subscriber, source, _loggerFactory.CreateLogger<ChangeToken>());
    }
}
