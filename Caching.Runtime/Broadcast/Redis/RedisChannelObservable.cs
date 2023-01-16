using System.Text;
using System.Threading.Channels;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisChannelObservable : IObservable<CloudEvent>
{
    private readonly Channel _channel;
    private readonly ISubscriber _subscriber;
    private readonly ILogger _logger;
    private readonly CloudEventFormatter _formatter;

    public RedisChannelObservable(Channel channel, ISubscriber subscriber, CloudEventFormatter formatter, ILogger logger)
        => (_channel, _subscriber, _formatter, _logger) = (channel, subscriber, formatter, logger);

    public IDisposable Subscribe(IObserver<CloudEvent> observer) =>
        new Subscription(_channel, _subscriber, observer, _formatter, _logger);


    private sealed class Subscription : IDisposable
    {
        private readonly RedisChannel _redisChannel;
        private readonly ISubscriber _subscriber;
        private readonly IObserver<CloudEvent> _observer;
        private readonly CloudEventFormatter _formatter;
        private readonly ILogger _logger;
        private readonly Action<RedisChannel, RedisValue> _handler;
        private bool _disposed;

        public Subscription(Channel channel, ISubscriber subscriber, IObserver<CloudEvent> observer, CloudEventFormatter formatter, ILogger logger)
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
                    var memory = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(value.ToString()));

                    var ev = _formatter.DecodeStructuredModeMessage(memory, null, null);
                    if (ev != null)
                    {
                        _logger.LogTrace("Event received. Id {}  Channel : {}", ev.Id, _redisChannel);
                        _observer.OnNext(ev);
                    }
                }
                catch(Exception ex)
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
