using System.Reactive.Subjects;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public sealed class RedisPubSubTopic<T> : ITopic<T>
     where T : IEvent
{
    private readonly RedisChannel _redisChannel;
    private readonly ISubject<T> _subject;
    private readonly IDatabase _database;
    private readonly ILogger _logger;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly IPolicyExecutor _writePolicy;
    private readonly RedisPubSubSubjectWriter<T> _subscriber;

    public TopicKey TopicKey { get; private set; }

    public RedisPubSubTopic(
        TopicKey topicKey,
        Func<ISubject<T>> subjectFactory,
        IDatabase database,
        ISubscriber subscriber,
        IEventFormatterProxy<T> formatter,
        IKeyResolver keyResolver,
        IPolicyHolder policyHolder,
        RedisCacheOptions options,
        CacheOptions cacheOptions,
        ILogger<RedisPubSubTopic<T>> logger)
    {
        TopicKey = topicKey;
        _formatter = formatter;
        _writePolicy = policyHolder.Write;
        _database = database;
        _logger = logger;        
        _subject = subjectFactory();
        var sourceUri = cacheOptions.SourceUri ?? CacheOptions.MachineUri;
        _redisChannel = GetRedisChannel(topicKey, keyResolver, options);
        _subscriber = new RedisPubSubSubjectWriter<T>(sourceUri, _redisChannel, subscriber, _subject, _formatter, _logger);
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
            await _writePolicy.ExecuteAsync(() => _database.PublishAsync(_redisChannel, messageString, CommandFlags.DemandMaster)).ConfigureAwait(false);
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

    private static RedisChannel GetRedisChannel(TopicKey topicKey, IKeyResolver keyResolver, RedisCacheOptions options)
    {
        var prefix = keyResolver.GetKey(options.RedisTypePrefixes.PubSub, options.Prefix);
        return keyResolver.GetKey((string)topicKey, prefix);
    }
}

