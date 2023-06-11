using System.Reactive.Subjects;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public sealed class RedisStreamsTopic<T> : ITopic<T>
     where T : IEvent
{
    private readonly RedisStreamsTopicOptions _options;
    private readonly RedisStreamSubjectWriter<T> _subscriber;
    private readonly CancellationTokenSource _stopTokenSource;
    private readonly RedisStreamContext _context;
    private readonly ISubject<T> _subject;
    private readonly IDatabase _database;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly IPolicyExecutor _writePolicy;
    private readonly ILogger _logger;

    public TopicKey TopicKey { get; private set; }

    public RedisStreamsTopic(
        TopicKey topicKey,
        Func<ISubject<T>> subjectFactory,
        IRedisConnection redisConnection,
        IEventFormatterProxy<T> formatter,
        IKeyResolver keyResolver,
        IPolicyHolder policyHolder,
        RedisStreamsTopicOptions options,
        RedisCacheOptions redisOptions,
        CacheOptions cacheOptions,
        ILogger<RedisStreamsTopic<T>> logger,
        CancellationToken stopToken)
    {
        TopicKey = topicKey;
        _formatter = formatter;
        _writePolicy = policyHolder.Write;
        _stopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _database = redisConnection.Connection.GetDatabase();
        _logger = logger;
        _subject = subjectFactory();
        _options = options;        
        _context = GetContext(topicKey, keyResolver, redisOptions, cacheOptions, options);
        EnsureStreamGroup();
        _subscriber = new RedisStreamSubjectWriter<T>(_context, redisConnection.Connection.GetDatabase(), _subject, _formatter, _logger, _stopTokenSource.Token);
    }

    public IDisposable Subscribe(IObserver<T> observer) =>
        _subject.Subscribe(observer);

    public async Task<bool> PublishAsync(T @event, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var messageString = _formatter.EncodeAsString(@event);
            var id = await _writePolicy.ExecuteAsync(() => _database.StreamAddAsync(
                _context.Topic,
                _context.FieldName,
                messageString,
                maxLength: _options.MaxLength,
                useApproximateMaxLength: true,
                flags: CommandFlags.DemandMaster)).ConfigureAwait(false);
            _logger.LogDebug("Published to topic {} event {} stream id {} ", TopicKey, @event.Id,  id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error when publishing to topic {} event {}", TopicKey, @event.Id);
            return false;
        }
    }

    public void Dispose()
    {
        _stopTokenSource.Cancel();
        _subject.OnCompleted();
        _subscriber.Dispose();
    }

    private void EnsureStreamGroup()
    {
        try
        {
            _ = _database.StreamCreateConsumerGroup(
                _context.Topic,
                _context.ConsumerGroup,
                StreamPosition.NewMessages);
        }
        catch (RedisServerException ex) when (ex.Message == Constants.ConsumerGroupNameExistsErrorMessage)
        {
            _logger.LogDebug("On Topic {} consumer group {} already exists", _context.Topic, _context.ConsumerGroup);
        }
    }

    private static RedisStreamContext GetContext(
        TopicKey topicKey,
        IKeyResolver keyResolver,
        RedisCacheOptions redisOptions,
        CacheOptions cacheOptions,
        RedisStreamsTopicOptions options)
    {
        var sourceUri = cacheOptions.SourceUri ?? CacheOptions.MachineUri;
        var sourceUriAsString = sourceUri.ToString().ToLowerInvariant();
        return new RedisStreamContext(
            Topic: GetTopicRedisKey(topicKey, keyResolver, redisOptions),
            FieldName: options.FieldName,
            ConsumerName: sourceUriAsString,
            ConsumerGroup: sourceUriAsString,
            SourceUri: sourceUri,
            PollBatchSize: options.PollBatchSize,
            PollInterval: options.PollInterval
            );
    }

    private static RedisKey GetTopicRedisKey(TopicKey topicKey, IKeyResolver keyResolver, RedisCacheOptions options)
    {
        var prefix = keyResolver.GetKey(options.RedisTypePrefixes.Streams, options.Prefix);
        return keyResolver.GetKey((string)topicKey, prefix);
    }
}
