using System.Reactive.Subjects;
using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public sealed class RedisStreamsTopic<T> : ITopic<T>
     where T : IEvent
{
    private readonly IRedisStreamKeyStrategy _redisStreamKeyStrategy;
    private readonly RedisStreamSubjectWriter<T> _subscriber;
    private readonly CancellationTokenSource _stopTokenSource;
    private readonly RedisStreamContext _context;
    private readonly ISubject<T> _subject;
    private readonly IConnectionState _connectionState;
    private readonly IRedisConnector _redis;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly IResiliencePipeline _write;
    private readonly ILogger _logger;
    private readonly ICachingTelemetryProvider _cachingTelemetryProvider;
    private readonly RedisStreamsTopicOptions _streamOptions;
    private readonly RedisConnectionOptions _redisConnectionOptions;
    private readonly EventDispatcher<T> _dispatcher;
    private readonly object _syncObj = new();
    private bool _disposed;
    private bool _consumerGroupCreated = false;

    public TopicKey TopicKey { get; }

    public EventHandler? OnDisposed { get; set; }

    public RedisStreamsTopic(
        TopicKey topicKey,
        IConnectionState connectionState,
        IRedisConnector redis,
        Func<ISubject<T>> subjectFactory,
        IEventFormatterProxy<T> formatter,
        IResiliencePipelineHolder resiliencePipelineHolder,
        RedisStreamsTopicOptions streamOptions,
        RedisConnectionOptions redisConnectionOptions,
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
        _redisConnectionOptions = redisConnectionOptions;
        _subject = subjectFactory();
        _redisStreamKeyStrategy = streamOptions.RedisStreamKeyStrategy ?? new PrefixStrategy(RedisTypePrefixes.Streams, cacheOptions);
        _context = GetContext(topicKey, cacheOptions, streamOptions);
        var channel = ChannelHelper.Create<T>(streamOptions.ConsumerCapacity < 0, streamOptions.ConsumerCapacity > 0 ? streamOptions.ConsumerCapacity : streamOptions.PollBatchSize , streamOptions.FullMode);
        _subscriber = new RedisStreamSubjectWriter<T>(_context, _connectionState, _redis, channel, _formatter, _logger, _cachingTelemetryProvider, redisProfiler, _stopTokenSource.Token);
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
            var messageString = _formatter.EncodeAsString(@event);
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
            }, token).ConfigureAwait(false);
            _cachingTelemetryProvider.TrackTopicWriteMetric(_context.Topic!, id);
            _logger.LogDebug("Published to topic {TopicKey} event {EventId} stream id {StreamId} ", TopicKey, @event.Id,  id);
            return !id.IsNull;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error when publishing to topic {TopicKey} event {EventId}", TopicKey, @event.Id);
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

    private void CreateConsumerGroup()
    {
        if (!_consumerGroupCreated)
        {
            lock (_syncObj)
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
            _logger.LogDebug("On Topic {Topic} consumer group {ConsumerGroup} already exists", _context.Topic, _context.ConsumerGroup);
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
            ProfilerEnabled: options.ProfilerEnabled ?? _redisConnectionOptions.ProfilerEnabled
            );
    }
}
