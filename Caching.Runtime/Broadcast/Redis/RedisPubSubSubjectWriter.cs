using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace UiPath.Platform.Caching.Broadcast.Redis;

internal sealed class RedisPubSubSubjectWriter<T> : IDisposable
    where T : IEvent
{
    private bool _disposed;
    private readonly Uri _sourceUri;
    private readonly RedisChannel _redisChannel;
    private readonly IRedisConnector _redis;
    private readonly ChannelWriter<T> _channelWriter;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly ILogger _logger;
    private readonly Action<RedisChannel, RedisValue> _handler;
    private readonly TimeSpan _timerPeriod;
    private readonly TimeSpan _timerDueTime;
    private readonly Timer _subscribeTimer;
    private Action? _unsubscribe;
    private int _subscribing;

    public RedisPubSubSubjectWriter(
        Uri sourceUri,
        RedisChannel redisChannel,
        IRedisConnector redis,
        ChannelWriter<T> channelWriter,
        IEventFormatterProxy<T> formatter,
        RedisPubSubTopicOptions options,
        ILogger logger)
    {
        _redis = redis;
        _channelWriter = channelWriter;
        _formatter = formatter;
        _logger = logger;
        _sourceUri = sourceUri;
        _redisChannel = redisChannel;
        _redis.OnReconnected += OnReconnected;
        _handler = (_, value) => OnMessage(value);
        _timerPeriod = options.SubscriberTimeout > TimeSpan.Zero ? options.SubscriberTimeout.Value : TimeSpan.FromMilliseconds(_redis.Subscriber.Multiplexer.TimeoutMilliseconds);
        _timerDueTime = options.SubscriberDueTime == null ? _timerPeriod.Multiply(0.5) : options.SubscriberDueTime.Value;
        _subscribeTimer = new Timer(Subscribe, null, _timerDueTime, _timerPeriod);
    }

    private void Subscribe(object? state)
    {
        if (Interlocked.CompareExchange(ref _subscribing, 1, 0) != 0)
        {
            return;
        }

        _logger.LogTrace("Subscribe channel: {}", _redisChannel);
        try
        {
            _unsubscribe?.Invoke();
            _unsubscribe = null;
            var subscriber = _redis.Subscriber;
            subscriber.Subscribe(_redisChannel, _handler);
            _unsubscribe = () => subscriber.Unsubscribe(_redisChannel, _handler, CommandFlags.FireAndForget);
            _subscribeTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subscribe error. Channel: {}", _redisChannel);
        }
        finally
        {
            Interlocked.Exchange(ref _subscribing, 0);
        }
    }

    private void OnReconnected(object? sender, EventArgs e)
    {
        if(_disposed)
        {
            return;
        }

        _subscribeTimer.Change(_timerDueTime, _timerPeriod);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _redis.OnReconnected -= OnReconnected;
            _subscribeTimer.Dispose();
            Unsubscribe();
        }
        _disposed = true;
    }

    private void OnMessage(RedisValue value)
    {
        if (!_disposed)
        {
            try
            {
                var ev = _formatter.Decode(value.ToString());
                if (ev == null)
                {
                    return;
                }

                if (ev.IsValid(_sourceUri))
                {
                    _logger.LogTrace("Event received. Id {}  Channel : {}", ev.Id, _redisChannel);
                    _channelWriter.TryWrite(ev);
                }
                else
                {
                    _logger.LogDebug("Skip event received. Id {}  Channel : {}", ev.Id, _redisChannel);
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
            _unsubscribe?.Invoke();
            _channelWriter.TryComplete();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unsubscribe error. Channel : {}", _redisChannel);
        }
    }
}
