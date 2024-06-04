using System.Reactive.Subjects;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public sealed class RedisStreamsTopic<T> : ITopic<T>
     where T : IEvent
{
    private const string ConsumerGroupNameExistsErrorMessage = "BUSYGROUP Consumer Group name already exists";

    private readonly IRedisStreamKeyStrategy _redisStreamKeyStrategy;
    private readonly RedisStreamSubjectWriter<T> _subscriber;
    private readonly CancellationTokenSource _stopTokenSource;
    private readonly RedisStreamContext _context;
    private readonly ISubject<T> _subject;
    private readonly IRedisConnector _redis;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly IPolicyExecutor _writePolicy;
    private readonly ILogger _logger;
    private readonly int? _maxLength;
    private readonly EventDispatcher<T> _dispatcher;
    private bool _disposed;

    public TopicKey TopicKey { get; }

    public EventHandler? OnDisposed { get; set; }

    public RedisStreamsTopic(
        TopicKey topicKey,
        IRedisConnector redis,
        Func<ISubject<T>> subjectFactory,
        IEventFormatterProxy<T> formatter,
        IPolicyHolder policyHolder,
        RedisStreamsTopicOptions options,
        CacheOptions cacheOptions,
        ILogger<RedisStreamsTopic<T>> logger,
        CancellationToken stopToken)
    {
        TopicKey = topicKey;
        _formatter = formatter;
        _writePolicy = policyHolder.Write;
        _stopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _redis = redis;
        _logger = logger;
        _subject = subjectFactory();
        _maxLength = options.MaxLength;
        _redisStreamKeyStrategy = options.RedisStreamKeyStrategy ?? new PrefixStrategy(RedisTypePrefixes.Streams, cacheOptions);
        _context = GetContext(topicKey, cacheOptions, options);
        EnsureStreamGroup();
        var channel = ChannelHelper.Create<T>(options.ConsumerCapacity < 0, options.ConsumerCapacity > 0 ? options.ConsumerCapacity : options.PollBatchSize , options.FullMode);
        _subscriber = new RedisStreamSubjectWriter<T>(_context, _redis, channel, _formatter, _logger, _stopTokenSource.Token);
        _dispatcher = new EventDispatcher<T>(topicKey, channel, _subject, _logger, _stopTokenSource.Token);
    }

    public IDisposable Subscribe(IObserver<T> observer) =>
        _subject.Subscribe(observer);

    public async ValueTask<bool> PublishAsync(T @event, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var messageString = _formatter.EncodeAsString(@event);
            var id = await _writePolicy.ExecuteAsync(() => _redis.Database.StreamAddAsync(
                _context.Topic,
                _context.FieldName,
                messageString,
                maxLength: _maxLength,
                useApproximateMaxLength: true,
                flags: CommandFlags.DemandMaster), token).ConfigureAwait(false);
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

    private void EnsureStreamGroup()
    {
        try
        {
            _ = _redis.Database.StreamCreateConsumerGroup(
                _context.Topic,
                _context.ConsumerGroup,
                StreamPosition.NewMessages);
        }
        catch (RedisServerException ex) when (ex.Message == ConsumerGroupNameExistsErrorMessage)
        {
            _logger.LogDebug(ex, "On Topic {Topic} consumer group {ConsumerGroup} already exists", _context.Topic, _context.ConsumerGroup);
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
            PollInterval: options.PollInterval
            );
    }
}
