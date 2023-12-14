using System.Reactive.Subjects;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public sealed class RedisPubSubTopic<T> : ITopic<T>
     where T : IEvent
{
    private readonly RedisChannel _redisChannel;
    private readonly CancellationTokenSource _stopTokenSource;
    private readonly ISubject<T> _subject;
    private readonly IRedisConnector _redis;
    private readonly ILogger _logger;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly IPolicyExecutor _writePolicy;
    private readonly RedisPubSubSubjectWriter<T> _subscriber;
    private readonly EventDispatcher<T> _dispatcher;
    private bool _disposed;

    public TopicKey TopicKey { get; }

    public EventHandler? OnDisposed { get; set; }

    public RedisPubSubTopic(
        TopicKey topicKey,
        Uri sourceUri,
        IRedisConnector redis,
        IRedisChannelStrategy redisChannelStrategy,
        Func<ISubject<T>> subjectFactory,
        IEventFormatterProxy<T> formatter,
        IPolicyHolder policyHolder,
        RedisPubSubTopicOptions options,
        ILogger<RedisPubSubTopic<T>> logger,
        CancellationToken stopToken)
    {
        TopicKey = topicKey;
        _redisChannel = redisChannelStrategy.GetRedisChannel(topicKey);
        _stopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _formatter = formatter;
        _writePolicy = policyHolder.Write;
        _redis = redis;
        _logger = logger;
        _subject = subjectFactory();
        var channel = ChannelHelper.Create<T>(options.ConsumerCapacity < 1, options.ConsumerCapacity, options.FullMode);
        _subscriber = new RedisPubSubSubjectWriter<T>(sourceUri, _redisChannel, _redis, channel, _formatter, _logger);
        _dispatcher = new EventDispatcher<T>(topicKey, channel, _subject, _logger, _stopTokenSource.Token);
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
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stopTokenSource?.Cancel();
        _stopTokenSource?.Dispose();
        _dispatcher.Dispose();
        _subject.OnCompleted();
        _subscriber.Dispose();
        OnDisposed?.Invoke(this, EventArgs.Empty);
    }
}
