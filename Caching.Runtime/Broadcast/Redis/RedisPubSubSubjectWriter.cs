using System.Reactive.Subjects;

namespace UiPath.Platform.Caching.Broadcast.Redis;

internal sealed class RedisPubSubSubjectWriter<T> : IDisposable
    where T : IEvent
{
    private bool _disposed;
    private readonly Uri _sourceUri;
    private readonly RedisChannel _redisChannel;
    private readonly ISubscriber _subscriber;
    private readonly ISubject<T> _subject;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly ILogger _logger;
    private readonly Action<RedisChannel, RedisValue> _handler;

    public RedisPubSubSubjectWriter(
        Uri sourceUri,
        RedisChannel redisChannel,
        ISubscriber subscriber,
        ISubject<T> subject,
        IEventFormatterProxy<T> formatter,
        ILogger logger)
    {
        _subscriber = subscriber;
        _subject = subject;
        _formatter = formatter;
        _logger = logger;
        _sourceUri = sourceUri;
        _redisChannel = redisChannel;
        _handler = (channel, value) => OnMessage(channel, value);
        _subscriber.Subscribe(_redisChannel, _handler);
    }
    public void Dispose()
    {
        if (!_disposed)
        {
            Unsubscribe();
        }
        _disposed = true;
    }

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

                if (ev.IsValid(_sourceUri))
                {
                    _logger.LogTrace("Event received. Id {}  Channel : {}", ev.Id, _redisChannel);
                    _subject.OnNext(ev);
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
            _subscriber?.Unsubscribe(_redisChannel, _handler);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unsubscribe error. Channel : {}", _redisChannel);
        }
    }
}
