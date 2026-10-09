using UiPath.Caching.Policies;

namespace UiPath.Caching.Broadcast.Redis;

public sealed partial class RedisPubSubTopic<T> : ITopic<T>
     where T : IEvent
{
    private readonly RedisChannel _redisChannel;
    private readonly CancellationTokenSource _stopTokenSource;
    private readonly IEventSubject<T> _subject;
    private readonly IRedisConnector _redis;
    private readonly ILogger _logger;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly IResiliencePipeline _write;
    private readonly RedisPubSubSubjectWriter<T> _subscriber;
    private readonly EventDispatcher<T> _dispatcher;
    private readonly IConnectionState _connectionState;
    private readonly RedisPubSubTopicOptions _options;
    private bool _disposed;

    public RedisPubSubTopic(
        TopicKey topicKey,
        Uri sourceUri,
        IConnectionState connectionState,
        IRedisConnector redis,
        IRedisChannelStrategy redisChannelStrategy,
        Func<IEventSubject<T>> subjectFactory,
        IEventFormatterProxy<T> formatter,
        IResiliencePipelineProvider resiliencePipelineProvider,
        RedisPubSubTopicOptions options,
        ILogger<RedisPubSubTopic<T>> logger,
        CancellationToken stopToken)
    {
        TopicKey = topicKey;
        _connectionState = connectionState;
        _redisChannel = redisChannelStrategy.GetRedisChannel(topicKey);
        _stopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _formatter = formatter;
        _write = resiliencePipelineProvider.Get(ResiliencePipelineNames.Write);
        _redis = redis;
        _logger = logger;
        _options = options;
        _subject = subjectFactory();
        var dropped = new DroppedEvents(topicKey, () => _subject.Invalidate(MissedEventsReason.Lost), _logger);
        var channel = ChannelHelper.Create<T>(options.ConsumerCapacity < 1, options.ConsumerCapacity, options.FullMode, _ => dropped.Dropped());
        _subscriber = new RedisPubSubSubjectWriter<T>(sourceUri, _redisChannel, _redis, channel, _formatter, options, _logger, () => _subject.Invalidate(MissedEventsReason.SubscriptionGap), dropped.Dropped);
        _dispatcher = new EventDispatcher<T>(topicKey, channel, _subject, _logger, _stopTokenSource.Token);
    }

    public TopicKey TopicKey { get; }

    public EventHandler? OnDisposed { get; set; }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        // Publications are missed while the Redis subscription is not in place, so the mark makes the next one that goes in
        // place expire this observer too. One that went in place while the observer was being added expired the others
        // before reaching it, so this observer is told here.
        var mark = _subscriber.MarkSubscribed();
        var subscription = _subject.Subscribe(observer);
        if (_subscriber.ActivatedSince(mark) && observer is IMissedEventsObserver missed)
        {
            TellMissed(missed, MissedEventsReason.SubscriptionGap);
        }

        return subscription;
    }

    public async ValueTask<bool> PublishAsync(T @event, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        if (!_connectionState.IsConnected)
        {
            return false;
        }

        try
        {
            RedisValue message = _formatter.Encode(@event);
            LogPublishing(TopicKey, @event.Id);
            var response = await _write.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self._redis.Database.PublishAsync(s.Self._redisChannel, s.Message, CommandFlags.DemandMaster).AsValueTask();
            },
            (Self: this, Message: message),
            defaultValue: -1,
            token).ConfigureAwait(false);
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
        _subject.Dispose();
        _subscriber.Dispose();
        OnDisposed?.Invoke(this, EventArgs.Empty);
    }

    internal RedisPubSubTopicOptions GetResolvedOptionsForTests() => _options;

    /// <summary>Tells an observer that joined as the subscription went in place that it may hold stale entries; what it throws is logged, not passed to Subscribe.</summary>
    private void TellMissed(IMissedEventsObserver missed, MissedEventsReason reason)
    {
        try
        {
            missed.OnEventsMissed(reason);
        }
        catch (Exception ex)
        {
            LogObserverMissedEventsFailed(ex, TopicKey);
        }
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Publishing to topic {TopicKey} event {EventId}")]
    private partial void LogPublishing(TopicKey topicKey, string? eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Observer threw in OnEventsMissed on topic {TopicKey}; it stays subscribed.")]
    private partial void LogObserverMissedEventsFailed(Exception ex, TopicKey topicKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error when publishing to Topic {TopicKey}")]
    private partial void LogPublishError(Exception ex, TopicKey topicKey);
}
