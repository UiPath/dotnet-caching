using System.Collections.Concurrent;

namespace UiPath.Platform.Caching.Broadcast.Redis;

internal class RedisChannelSubscriber : IChannelSubscriber
{
    private readonly ConcurrentDictionary<Channel, IObservable<CloudEvent>> _channels = new();
    private readonly ISubscriber _subscriber;
    private readonly ILogger<RedisChannelSubscriber> _logger;
    private readonly CloudEventFormatter _formatter;

    public RedisChannelSubscriber(ISubscriber subscriber, CloudEventFormatter formatter, ILogger<RedisChannelSubscriber> logger)
         => (_subscriber, _formatter, _logger) = (subscriber, formatter, logger);

    public IDisposable Subscribe(Channel channel, IObserver<CloudEvent> observer)
    {
        try
        {
            var observable = _channels.GetOrAdd(channel, c =>
            {
                _logger.LogTrace("Observe channel {}", channel);
                return new RedisChannelObservable(channel, _subscriber, _formatter, _logger);
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
