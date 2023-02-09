using System.Collections.Concurrent;

namespace UiPath.Platform.Caching.Broadcast.Redis;

internal class RedisChannelSubscriber<T> : IChannelSubscriber<T> where T : class, IPubSubEvent
{
    private readonly ConcurrentDictionary<Channel, IObservable<T>> _channels = new();
    private readonly ISubscriber _subscriber;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RedisChannelSubscriber<T>> _logger;
    private readonly IEventFormatterProxy<T> _formatter;

    public RedisChannelSubscriber(ISubscriber subscriber, IEventFormatterProxy<T> formatter, ILoggerFactory loggerFactory)
         => (_subscriber, _formatter, _loggerFactory, _logger) = (subscriber, formatter, loggerFactory, loggerFactory.CreateLogger<RedisChannelSubscriber<T>>());

    public IDisposable Subscribe(Channel channel, IObserver<T> observer)
    {
        try
        {
            var observable = _channels.GetOrAdd(channel, c =>
            {
                _logger.LogTrace("Observe channel {}", channel);
                return new RedisChannelObservable<T>(channel, _subscriber, _formatter, _loggerFactory);
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
