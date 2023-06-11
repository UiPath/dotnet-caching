using System.Collections.Concurrent;
using System.Reactive.Subjects;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public sealed class RedisStreamsTopicProvider : ITopicProvider
{
    private readonly RedisStreamsTopicOptions _redisStreamsTopicOptions;
    private readonly RedisCacheOptions _redisCacheOptions;
    private readonly CacheOptions _cacheOptions;

    private readonly ConcurrentDictionary<TopicKey, Lazy<ITopic<ICacheEvent>>> _topics = new();

    private readonly IRedisConnection _redisConnection;
    private readonly IEventFormatterProxy<ICacheEvent> _formatter;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IKeyResolver _keyResolver;
    private readonly IPolicyHolder _policyHolder;
    private readonly CancellationTokenSource _stopTokenSource = new();

    public RedisStreamsTopicProvider(
        IOptions<RedisStreamsTopicOptions> redisStreamsTopicOptionsAccessor,
        IOptions<RedisCacheOptions> redisCacheOptionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        IRedisConnection redisConnection,
        IEventFormatterProxy<ICacheEvent> formatter,
        IKeyResolver keyResolver,
        IPolicyHolder policyHolder,
        ILoggerFactory loggerFactory)
    {
        _redisStreamsTopicOptions = redisStreamsTopicOptionsAccessor.Value;
        _redisCacheOptions = redisCacheOptionsAccessor.Value;
        _cacheOptions = cacheOptionsAccessor.Value;
        _keyResolver = keyResolver;
        _policyHolder = policyHolder;
        _redisConnection = redisConnection;
        _formatter = formatter;
        _loggerFactory = loggerFactory;
    }

    public string Name => KnownTopicNames.RedisStreams;

    public bool Enabled => _redisStreamsTopicOptions.Enabled;

    public ITopic<ICacheEvent> CreateTopic(TopicKey topicKey) =>
        _topics.GetOrAdd(topicKey, tk => new Lazy<ITopic<ICacheEvent>>(() => CreateInternalTopic(tk))).Value;

    public void Dispose()
    {
        _stopTokenSource.Cancel();
        _topics.Clear();
    }

    public ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey) =>
        new RedisStreamsTopic<ICacheEvent>(
            topicKey,
            () => new Subject<ICacheEvent>(),
            _redisConnection,
            _formatter,
            _keyResolver,
            _policyHolder,
            _redisStreamsTopicOptions,
            _redisCacheOptions,
            _cacheOptions,
            _loggerFactory.Create<RedisStreamsTopic<ICacheEvent>>(),
            _stopTokenSource.Token);
}
