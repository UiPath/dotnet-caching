using System.Threading.Channels;

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

    public RedisPubSubSubjectWriter(
        Uri sourceUri,
        RedisChannel redisChannel,
        IRedisConnector redis,
        ChannelWriter<T> channelWriter,
        IEventFormatterProxy<T> formatter,
        ILogger logger)
    {
        _redis = redis;
        _channelWriter = channelWriter;
        _formatter = formatter;
        _logger = logger;
        _sourceUri = sourceUri;
        _redisChannel = redisChannel;
        _handler = (channel, value) => OnMessage(value);
        _redis.Subscriber.Subscribe(_redisChannel, _handler);
        _redis.OnReconnected += OnReconnected;
    }

    private void OnReconnected(object? sender, EventArgs e) =>
        _redis.Subscriber.Subscribe(_redisChannel, _handler);

    public void Dispose()
    {
        if (!_disposed)
        {
            _redis.OnReconnected -= OnReconnected;
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
            _redis.Subscriber.Unsubscribe(_redisChannel, _handler);
            _channelWriter.TryComplete();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unsubscribe error. Channel : {}", _redisChannel);
        }
    }
}
