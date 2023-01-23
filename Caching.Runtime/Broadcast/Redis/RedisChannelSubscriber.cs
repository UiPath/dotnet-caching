using System.Collections.Concurrent;

namespace UiPath.Platform.Caching.Broadcast.Redis;

internal class RedisChannelSubscriber : IChannelSubscriber
{
    private readonly ConcurrentDictionary<Channel, IObservable<IClearCacheEvent>> _channels = new();
    private readonly ISubscriber _subscriber;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RedisChannelSubscriber> _logger;
    private readonly IEventFormatterProxy _formatter;

    public RedisChannelSubscriber(ISubscriber subscriber, IEventFormatterProxy formatter, ILoggerFactory loggerFactory)
         => (_subscriber, _formatter, _loggerFactory, _logger) = (subscriber, formatter, loggerFactory, loggerFactory.CreateLogger<RedisChannelSubscriber>());

    public IDisposable Subscribe(Channel channel, IObserver<IClearCacheEvent> observer)
    {
        try
        {
            var observable = _channels.GetOrAdd(channel, c =>
            {
                _logger.LogTrace("Observe channel {}", channel);
                return new RedisChannelObservable(channel, _subscriber, _formatter, _loggerFactory);
            });
            _logger.LogTrace("Subscribe to channel {}", channel);
            return observable.Subscribe(observer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while subscribing on {}", channel);
            throw;
        }
    }
}
