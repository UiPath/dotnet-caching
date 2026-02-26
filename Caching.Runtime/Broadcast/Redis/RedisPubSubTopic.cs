using System.Reactive.Subjects;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public sealed partial class RedisPubSubTopic<T> : ITopic<T>
     where T : IEvent
{
    private readonly RedisChannel _redisChannel;
    private readonly CancellationTokenSource _stopTokenSource;
    private readonly ISubject<T> _subject;
    private readonly IRedisConnector _redis;
    private readonly ILogger _logger;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly IResiliencePipeline _write;
    private readonly RedisPubSubSubjectWriter<T> _subscriber;
    private readonly EventDispatcher<T> _dispatcher;
    private readonly IConnectionState _connectionState;
    private bool _disposed;

    public TopicKey TopicKey { get; }

    public EventHandler? OnDisposed { get; set; }

    public RedisPubSubTopic(
        TopicKey topicKey,
        Uri sourceUri,
        IConnectionState connectionState,
        IRedisConnector redis,
        IRedisChannelStrategy redisChannelStrategy,
        Func<ISubject<T>> subjectFactory,
        IEventFormatterProxy<T> formatter,
        IResiliencePipelineHolder resiliencePipelineHolder,
        RedisPubSubTopicOptions options,
        ILogger<RedisPubSubTopic<T>> logger,
        CancellationToken stopToken)
    {
        TopicKey = topicKey;
        _connectionState = connectionState;
        _redisChannel = redisChannelStrategy.GetRedisChannel(topicKey);
        _stopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _formatter = formatter;
        _write = resiliencePipelineHolder.Write;
        _redis = redis;
        _logger = logger;
        _subject = subjectFactory();
        var channel = ChannelHelper.Create<T>(options.ConsumerCapacity < 1, options.ConsumerCapacity, options.FullMode);
        _subscriber = new RedisPubSubSubjectWriter<T>(sourceUri, _redisChannel, _redis, channel, _formatter, options, _logger);
        _dispatcher = new EventDispatcher<T>(topicKey, channel, _subject, _logger, _stopTokenSource.Token);
    }

    public IDisposable Subscribe(IObserver<T> observer) =>
        _subject.Subscribe(observer);

    public async ValueTask<bool> PublishAsync(T @event, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        if (!_connectionState.IsConnected)
        {
            return false;
        }

        try
        {
            var messageString = _formatter.EncodeAsString(@event);
            LogPublishing(TopicKey, @event.Id);
            var response = await _write.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await _redis.Database.PublishAsync(_redisChannel, messageString, CommandFlags.DemandMaster).ConfigureAwait(false);
            }, defaultValue: -1, token).ConfigureAwait(false);
            return response >= 0;
        }
        catch (Exception ex)
        {
            LogPublishError(ex, TopicKey);
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

        if (_subject is IDisposable disposableSubject)
        {
            disposableSubject.Dispose();
        }

        OnDisposed?.Invoke(this, EventArgs.Empty);
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Publishing to topic {TopicKey} event {EventId}")]
    private partial void LogPublishing(TopicKey topicKey, string? eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error when publishing to Topic {TopicKey}")]
    private partial void LogPublishError(Exception ex, TopicKey topicKey);
}
