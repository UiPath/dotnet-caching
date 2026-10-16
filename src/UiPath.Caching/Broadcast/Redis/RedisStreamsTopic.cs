using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast.Redis;

public sealed partial class RedisStreamsTopic<T> : ITopic<T>
     where T : IEvent
{
    private readonly IRedisStreamKeyStrategy _redisStreamKeyStrategy;
    private readonly RedisStreamSubjectWriter<T> _subscriber;
    private readonly CancellationTokenSource _stopTokenSource;
    private readonly RedisStreamContext _context;
    private readonly IEventSubject<T> _subject;
    private readonly IConnectionState _connectionState;
    private readonly IRedisConnector _redis;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly IResiliencePipeline _write;
    private readonly ILogger _logger;
    private readonly ICachingTelemetryProvider _cachingTelemetryProvider;
    private readonly RedisStreamsTopicOptions _streamOptions;
    private readonly EventDispatcher<T> _dispatcher;
    private readonly RedisStreamNotifyChannel? _notifyChannel;
    private readonly RedisChannel? _notifyRedisChannel;
    private readonly SignalingFetchWaiter _waiter;
    private readonly Lock _syncObj = new();
    // Half of MaxLength; 0 = no trim.
    private readonly long _wakeEvery;
    private long _published;
    private bool _disposed;
    private volatile bool _consumerGroupCreated;

    public RedisStreamsTopic(
        TopicKey topicKey,
        IConnectionState connectionState,
        IRedisConnector redis,
        Func<IEventSubject<T>> subjectFactory,
        IEventFormatterProxy<T> formatter,
        IResiliencePipelineProvider resiliencePipelineProvider,
        RedisStreamsTopicOptions streamOptions,
        CacheOptions cacheOptions,
        ILogger<RedisStreamsTopic<T>> logger,
        ICachingTelemetryProvider cachingTelemetryProvider,
        IRedisProfiler redisProfiler,
        CancellationToken stopToken)
    {
        TopicKey = topicKey;
        _connectionState = connectionState;
        _formatter = formatter;
        _write = resiliencePipelineProvider.Get(ResiliencePipelineNames.Write);
        _stopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _redis = redis;
        _logger = logger;
        _cachingTelemetryProvider = cachingTelemetryProvider;
        _streamOptions = streamOptions;
        _subject = subjectFactory();
        _redisStreamKeyStrategy = streamOptions.RedisStreamKeyStrategy ?? new PrefixStrategy(RedisKeyspaces.Streams, cacheOptions);
        _context = GetContext(topicKey, cacheOptions, streamOptions);
        _waiter = new SignalingFetchWaiter(streamOptions.PollInterval);
        _wakeEvery = streamOptions.MaxLength is { } maxLength ? Math.Max(1, maxLength / 2) : 0;
        if (_streamOptions.NotifyEnabled)
        {
            var notifyChannelStrategy = streamOptions.NotifyChannelStrategy
                ?? StreamSuffixChannel.Create(_redisStreamKeyStrategy, cacheOptions, streamOptions.NotifyChannelName, streamOptions.NotifyShardedPubSub);
            _notifyRedisChannel = notifyChannelStrategy.GetRedisChannel(topicKey);
            _notifyChannel = new RedisStreamNotifyChannel(
                _notifyRedisChannel.Value,
                _redis,
                _logger,
                _waiter,
                streamOptions.NotifySubscriberTimeout,
                streamOptions.NotifySubscriberDueTime,
                _context.ConsumerName);
        }
        var capacity = ChannelHelper.CalculateBoundedCapacity(streamOptions.ConsumerCapacity, streamOptions.PollBatchSize);
        // Run later on the pool, by which time the writer the drop reports through is in place.
        var dropped = new DroppedEvents(topicKey, () => _subscriber!.ReportLoss(), _logger);
        var channel = ChannelHelper.Create<T>(streamOptions.ConsumerCapacity < 0, capacity, streamOptions.FullMode, _ => dropped.Dropped());
        _subscriber = new RedisStreamSubjectWriter<T>(_context, _connectionState, _redis, channel, _formatter, _logger, _cachingTelemetryProvider, redisProfiler, _waiter, () => _subject.Invalidate(MissedEventsReason.Lost), _stopTokenSource.Token);
        _dispatcher = new EventDispatcher<T>(topicKey, channel, _subject, _logger, _stopTokenSource.Token);
    }

    public TopicKey TopicKey { get; }

    public EventHandler? OnDisposed { get; set; }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        this.ThrowIfDisposed(_disposed);
        CreateConsumerGroup();

        // Marked first, so a loss reported next expires this observer too; one reported while it joined may have expired
        // the others before it, so it is told itself.
        var mark = _subscriber.MarkSubscribed();
        var subscription = _subject.Subscribe(observer);
        if (_subscriber.LostSince(mark) && observer is IMissedEventsObserver missed)
        {
            TellMissed(missed, MissedEventsReason.Lost);
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
            var id = await _write.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self._redis.Database.StreamAddAsync(
                    s.Self._context.Topic,
                    s.Self._context.FieldName,
                    s.Message,
                    maxLength: s.Self._streamOptions.MaxLength,
                    useApproximateMaxLength: true,
                    flags: CommandFlags.DemandMaster).AsValueTask();
            },
            (Self: this, Message: message),
            defaultValue: RedisValue.Null,
            token).ConfigureAwait(false);
            _cachingTelemetryProvider.TrackTopicWriteMetric(_context.Topic.ToString(), id);
            if (!id.IsNull)
            {
                Ring();
            }

            LogPublished(TopicKey, @event.Id, id);
            return !id.IsNull;
        }
        catch (Exception ex)
        {
            LogPublishError(ex, TopicKey, @event.Id);
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
        _notifyChannel?.Dispose();
        _waiter.Dispose();
        OnDisposed?.Invoke(this, EventArgs.Empty);
    }

    internal RedisStreamsTopicOptions GetResolvedOptionsForTests() => _streamOptions;

    /// <summary>Wakes the other nodes, and this one every <see cref="_wakeEvery"/> own entries: nothing else rings for those, and a burst of them could be trimmed unread.</summary>
    private void Ring()
    {
        if (_notifyRedisChannel.HasValue)
        {
            try
            {
                // Fire-and-forget: only a send failure surfaces here.
                _ = _redis.Database.PublishAsync(_notifyRedisChannel.Value, _context.ConsumerName, CommandFlags.FireAndForget);
            }
            catch (Exception ex)
            {
                LogNotifyPublishError(ex, TopicKey);
            }
        }

        if (_wakeEvery > 0 && Interlocked.Increment(ref _published) % _wakeEvery == 0)
        {
            _waiter.Signal();
        }
    }

    /// <summary>Isolated like the subject's own notifications, so a throwing observer cannot fail the subscribe that registered it.</summary>
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

    private void CreateConsumerGroup()
    {
        if (!_consumerGroupCreated)
        {
            lock (_syncObj)
            {
#pragma warning disable S2589 // double-checked locking with a volatile field; outer check is racy by design
                if (!_consumerGroupCreated)
#pragma warning restore S2589
                {
                    _consumerGroupCreated = EnsureStreamGroup();
                }
            }
        }
    }

    private bool EnsureStreamGroup()
    {
        try
        {
            return ConsumerGroups.Create(_redis.Database, _context.Topic, _context.ConsumerGroup, StreamPosition.NewMessages);
        }
        catch (RedisServerException ex) when (ex.Message == StreamConstants.ConsumerGroupNameExistsErrorMessage)
        {
            LogConsumerGroupExists(_context.Topic, _context.ConsumerGroup);
            return true;
        }
    }

    private RedisStreamContext GetContext(
        TopicKey topicKey,
        CacheOptions cacheOptions,
        RedisStreamsTopicOptions options)
    {
        var sourceUri = cacheOptions.SourceUri ?? CacheOptions.MachineUri;
        var sourceUriAsString = sourceUri.ToString().ToLowerInvariant();
        return new RedisStreamContext(
            Topic: _redisStreamKeyStrategy.GetRedisKey(topicKey),
            FieldName: options.FieldName,
            ConsumerName: sourceUriAsString,
            ConsumerGroup: sourceUriAsString,
            SourceUri: sourceUri,
            PollBatchSize: options.PollBatchSize,
            PollInterval: options.PollInterval,
            ProfilerEnabled: options.ProfilerEnabled,
            EmitStreamReceivedEvent: options.EmitStreamReceivedEvent
            );
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Published to topic {TopicKey} event {EventId} stream id {StreamId}")]
    private partial void LogPublished(TopicKey topicKey, string? eventId, RedisValue streamId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Observer threw in OnEventsMissed on topic {TopicKey}; it stays subscribed.")]
    private partial void LogObserverMissedEventsFailed(Exception ex, TopicKey topicKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error when publishing to topic {TopicKey} event {EventId}")]
    private partial void LogPublishError(Exception ex, TopicKey topicKey, string? eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Best-effort notify doorbell publish failed for topic {TopicKey}")]
    private partial void LogNotifyPublishError(Exception ex, TopicKey topicKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "On Topic {Topic} consumer group {ConsumerGroup} already exists")]
    private partial void LogConsumerGroupExists(RedisKey topic, RedisValue consumerGroup);
}
