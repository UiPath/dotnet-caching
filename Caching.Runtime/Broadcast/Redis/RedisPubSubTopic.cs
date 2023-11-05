using System.Reactive.Subjects;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public sealed class RedisPubSubTopic<T> : ITopic<T>
     where T : IEvent
{
    private readonly RedisChannel _redisChannel;
    private readonly ISubject<T> _subject;
    private readonly IRedisConnector _redis;
    private readonly ILogger _logger;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly IPolicyExecutor _writePolicy;
    private readonly RedisPubSubSubjectWriter<T> _subscriber;

    public TopicKey TopicKey { get; private set; }

    public RedisPubSubTopic(
        TopicKey topicKey,
        Uri sourceUri,
        IRedisConnector redis,
        IRedisChannelStrategy redisChannelStrategy,
        Func<ISubject<T>> subjectFactory,
        IEventFormatterProxy<T> formatter,
        IPolicyHolder policyHolder,
        ILogger<RedisPubSubTopic<T>> logger)
    {
        TopicKey = topicKey;
        _redisChannel = redisChannelStrategy.GetRedisChannel(topicKey);
        _formatter = formatter;
        _writePolicy = policyHolder.Write;
        _redis = redis;
        _logger = logger;
        _subject = subjectFactory();
        _subscriber = new RedisPubSubSubjectWriter<T>(sourceUri, _redisChannel, _redis, _subject, _formatter, _logger);
    }

    public IDisposable Subscribe(IObserver<T> observer) =>
        _subject.Subscribe(observer);

    public async Task<bool> PublishAsync(T @event, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var messageString = _formatter.EncodeAsString(@event);
            _logger.LogTrace("Publishing to topic {} event {}", TopicKey, @event.Id);
            await _writePolicy.ExecuteAsync(() => _redis.Database.PublishAsync(_redisChannel, messageString, CommandFlags.DemandMaster)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error when publishing to Topic {}", TopicKey);
            return false;
        }
    }

    public void Dispose()
    { 
        _subscriber.Dispose();
        _subject.OnCompleted();
    }
}

