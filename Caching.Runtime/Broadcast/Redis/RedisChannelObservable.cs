namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisChannelObservable<T> : IObservable<T> where T : class, IPubSubEvent
{
    private readonly Channel _channel;
    private readonly ISubscriber _subscriber;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IEventFormatterProxy<T> _formatter;

    public RedisChannelObservable(Channel channel, ISubscriber subscriber, IEventFormatterProxy<T> formatter, ILoggerFactory loggerFactory)
        => (_channel, _subscriber, _formatter, _loggerFactory) = (channel, subscriber, formatter, loggerFactory);

    public IDisposable Subscribe(IObserver<T> observer) =>
        new Subscription(_channel, _subscriber, observer, _formatter, _loggerFactory.CreateLogger<Subscription>());


    private sealed class Subscription : IDisposable
    {
        private readonly RedisChannel _redisChannel;
        private readonly ISubscriber _subscriber;
        private readonly IObserver<T> _observer;
        private readonly IEventFormatterProxy<T> _formatter;
        private readonly ILogger<Subscription> _logger;
        private readonly Action<RedisChannel, RedisValue> _handler;
        private bool _disposed;

        public Subscription(Channel channel, ISubscriber subscriber, IObserver<T> observer, IEventFormatterProxy<T> formatter, ILogger<Subscription> logger)
        {
            _redisChannel = (string)channel;
            _subscriber = subscriber;
            _observer = observer;
            _formatter = formatter;
            _logger = logger;
            _handler = (channel, value) => OnMessage(channel, value);
            _subscriber.Subscribe(_redisChannel, _handler);
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Unsubscribe();
                }
            }
            _disposed = true;
        }

        public void Dispose() =>
            Dispose(true);

        private void OnMessage(RedisChannel channel, RedisValue value)
        {
            if (!_disposed && channel == _redisChannel)
            {
                try
                {
                    var ev = _formatter.Decode(value.ToString());
                    if (ev == null)
                    {
                        return;
                    }

                    if (ev.IsValid())
                    {
                        _logger.LogTrace("Event received. Id {}  Channel : {}", ev.Id, _redisChannel);
                        _observer.OnNext(ev);
                    }
                    else
                    {
                        _logger.LogDebug("Event received. Id {}  Channel : {}", ev.Id, _redisChannel);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OnMessage error. Channel : {}", _redisChannel);
                }
            }
        }

        private void Unsubscribe()
        {
            _logger.LogTrace("Unsubscribe channel: {}", _redisChannel);
            try
            {
                _subscriber?.Unsubscribe(_redisChannel, _handler);
                _observer.OnCompleted();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unsubscribe error. Channel : {}", _redisChannel);
            }
        }
    }
}
