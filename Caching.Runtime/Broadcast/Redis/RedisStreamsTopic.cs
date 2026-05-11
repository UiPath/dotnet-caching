using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Broadcast.Redis;

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
    private readonly IFetchWaiter _waiter;
#if NET9_0_OR_GREATER
    private readonly Lock _syncObj = new();
#else
    private readonly object _syncObj = new();
#endif
    private bool _disposed;
    private volatile bool _consumerGroupCreated;

    public TopicKey TopicKey { get; }

    public EventHandler? OnDisposed { get; set; }

    public RedisStreamsTopic(
        TopicKey topicKey,
        IConnectionState connectionState,
        IRedisConnector redis,
        Func<IEventSubject<T>> subjectFactory,
        IEventFormatterProxy<T> formatter,
        IResiliencePipelineHolder resiliencePipelineHolder,
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
        _write = resiliencePipelineHolder.Write;
        _stopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _redis = redis;
        _logger = logger;
        _cachingTelemetryProvider = cachingTelemetryProvider;
        _streamOptions = streamOptions;
        _subject = subjectFactory();
        _redisStreamKeyStrategy = streamOptions.RedisStreamKeyStrategy ?? new PrefixStrategy(RedisTypePrefixes.Streams, cacheOptions);
        if (_streamOptions.NotifyEnabled)
        {
            var notifyChannelStrategy = streamOptions.NotifyChannelStrategy
                ?? StreamSuffixChannel.Create(_redisStreamKeyStrategy, cacheOptions, streamOptions.NotifyChannelName, streamOptions.NotifyShardedPubSub);
            _notifyRedisChannel = notifyChannelStrategy.GetRedisChannel(topicKey);
            var signaling = new SignalingFetchWaiter(streamOptions.PollInterval);
            _waiter = signaling;
            _notifyChannel = new RedisStreamNotifyChannel(
                _notifyRedisChannel.Value,
                _redis,
                _logger,
                signaling,
                streamOptions.NotifySubscriberTimeout,
                streamOptions.NotifySubscriberDueTime);
        }
        else
        {
            _waiter = new TimedFetchWaiter(streamOptions.PollInterval);
        }
        _context = GetContext(topicKey, cacheOptions, streamOptions);
        var channel = ChannelHelper.Create<T>(streamOptions.ConsumerCapacity < 0, streamOptions.ConsumerCapacity > 0 ? streamOptions.ConsumerCapacity : streamOptions.PollBatchSize , streamOptions.FullMode);
        _subscriber = new RedisStreamSubjectWriter<T>(_context, _connectionState, _redis, channel, _formatter, _logger, _cachingTelemetryProvider, redisProfiler, _waiter, _stopTokenSource.Token);
        _dispatcher = new EventDispatcher<T>(topicKey, channel, _subject, _logger, _stopTokenSource.Token);
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        this.ThrowIfDisposed(_disposed);
        CreateConsumerGroup();
        return _subject.Subscribe(observer);
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
            RedisValue messageString = _formatter.EncodeAsString(@event);
            var id = await _write.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await _redis.Database.StreamAddAsync(
                    _context.Topic,
                    _context.FieldName,
                    messageString,
                    maxLength: _streamOptions.MaxLength,
                    useApproximateMaxLength: true,
                    flags: CommandFlags.DemandMaster).ConfigureAwait(false);
            }, defaultValue: RedisValue.Null, token).ConfigureAwait(false);
            _cachingTelemetryProvider.TrackTopicWriteMetric(_context.Topic!, id);
            if (_notifyRedisChannel.HasValue && !id.IsNull)
            {
                try
                {
                    _ = _redis.Database.PublishAsync(_notifyRedisChannel.Value, RedisValue.EmptyString, CommandFlags.FireAndForget)
                        .ContinueWith(t => LogNotifyPublishError(t.Exception!, TopicKey), TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                }
                catch (Exception ex)
                {
                    LogNotifyPublishError(ex, TopicKey);
                }
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

    private void CreateConsumerGroup()
    {
        if (!_consumerGroupCreated)
        {
#if NET9_0_OR_GREATER
            using (_syncObj.EnterScope())
#else
            lock (_syncObj)
#endif
            {
                if (!_consumerGroupCreated)
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
            return _redis.Database.StreamCreateConsumerGroup(
                _context.Topic,
                _context.ConsumerGroup,
                StreamPosition.NewMessages);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error when publishing to topic {TopicKey} event {EventId}")]
    private partial void LogPublishError(Exception ex, TopicKey topicKey, string? eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Best-effort notify doorbell publish failed for topic {TopicKey}")]
    private partial void LogNotifyPublishError(Exception ex, TopicKey topicKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "On Topic {Topic} consumer group {ConsumerGroup} already exists")]
    private partial void LogConsumerGroupExists(RedisKey topic, RedisValue consumerGroup);
}
