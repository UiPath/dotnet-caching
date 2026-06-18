using System.Threading.Channels;

namespace UiPath.Caching.Broadcast.Redis;

internal sealed partial class RedisPubSubSubjectWriter<T> : IDisposable
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

        LogSubscribeChannel(_redisChannel);
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
            LogSubscribeError(ex, _redisChannel);
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
                if (ev is null)
                {
                    return;
                }

                LogEventReceived(ev.Id, _redisChannel);
                if (ev.IsValid())
                {
                    if (ev.SameSource(_sourceUri))
                    {
                        LogEventFromCurrentSource(ev.Id, _redisChannel);
                    }
                    else
                    {
                        _channelWriter.TryWrite(ev);
                    }
                }
                else
                {
                    LogEventInvalid(ev.Id, _redisChannel);
                }
            }
            catch (Exception ex)
            {
                LogOnMessageError(ex, _redisChannel);
            }
        }
    }

    private void Unsubscribe()
    {
        LogUnsubscribeChannel(_redisChannel);
        try
        {
            _unsubscribe?.Invoke();
            _channelWriter.TryComplete();
        }
        catch (Exception ex)
        {
            LogUnsubscribeError(ex, _redisChannel);
        }
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Subscribe channel: {RedisChannel}")]
    private partial void LogSubscribeChannel(RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Error, Message = "Subscribe error. Channel: {RedisChannel}")]
    private partial void LogSubscribeError(Exception ex, RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Event received. Id {EventId}  Channel : {RedisChannel}")]
    private partial void LogEventReceived(string? eventId, RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Event from current source. Id {EventId}  Channel : {RedisChannel}")]
    private partial void LogEventFromCurrentSource(string? eventId, RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event invalid. Id {EventId}  Channel : {RedisChannel}")]
    private partial void LogEventInvalid(string? eventId, RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Error, Message = "OnMessage error. Channel : {RedisChannel}")]
    private partial void LogOnMessageError(Exception ex, RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Unsubscribe channel: {RedisChannel}")]
    private partial void LogUnsubscribeChannel(RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unsubscribe error. Channel : {RedisChannel}")]
    private partial void LogUnsubscribeError(Exception ex, RedisChannel redisChannel);
}
