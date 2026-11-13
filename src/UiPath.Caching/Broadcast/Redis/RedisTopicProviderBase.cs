using System.Collections.Concurrent;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast.Redis;

public abstract partial class RedisTopicProviderBase(
    IRedisConnector redis,
    ICachingTelemetryProvider telemetryProvider,
    IRedisProfiler redisProfiler,
    ILoggerFactory loggerFactory,
    bool connectionMonitorEnabled)
    : ITopicProvider, IConnectionState
{

    private readonly ConcurrentDictionary<TopicKey, Lazy<ITopic<ICacheEvent>>> _topics = new();

    private readonly CancellationTokenSource _stopTokenSource = new();
    private readonly ILogger _logger = loggerFactory.CreateLogger<RedisTopicProviderBase>();
    private volatile bool _disposed;

    public event EventHandler? OnConnectionFailed
    {
        add => ConnectionState.OnConnectionFailed += value;
        remove => ConnectionState.OnConnectionFailed -= value;
    }

    public event EventHandler? OnConnectionRestored
    {
        add => ConnectionState.OnConnectionRestored += value;
        remove => ConnectionState.OnConnectionRestored -= value;
    }

    public event EventHandler? OnReconnected
    {
        add => ConnectionState.OnReconnected += value;
        remove => ConnectionState.OnReconnected -= value;
    }

    public bool IsConnected => ConnectionState.IsConnected;

    public abstract string Name { get; }

    public abstract bool Enabled { get; }

    public ICollection<TopicKey> Keys => _topics.Keys;

    protected IRedisConnector Redis { get; } = redis;

    protected IConnectionState ConnectionState { get; } = connectionMonitorEnabled ? redis : NullConnectionStateMonitor.Instance;

    protected ICachingTelemetryProvider Telemetry { get; } = telemetryProvider;

    protected IRedisProfiler Profiler { get; } = redisProfiler;

    protected ILoggerFactory LoggerFactory { get; } = loggerFactory;

    // Read through a property: Dispose on another thread can set it between the two checks in Create.
    private bool IsDisposed => _disposed;

    public ITopic<ICacheEvent> Create(TopicKey topicKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var topic = _topics.GetOrAdd(topicKey, tk => new Lazy<ITopic<ICacheEvent>>(() => {
            var t = CreateInternalTopic(tk);
            t.OnDisposed += RemoveTopic;
            return t;
        })).Value;
        if (IsDisposed)
        {
            // The provider was disposed while this topic was being created, so DisposeTopics skipped it.
            topic.OnDisposed -= RemoveTopic;
            topic.Dispose();
            ObjectDisposedException.ThrowIf(true, this);
        }
        return topic;
    }

    public void Remove(TopicKey topicKey)
    {
        if (_topics.TryRemove(topicKey, out var lazyTopic) && lazyTopic.IsValueCreated)
        {
            var topic = lazyTopic.Value;
            topic.OnDisposed -= RemoveTopic;
            topic.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The topics made so far and not removed, for the node node list.</summary>
    internal IEnumerable<ITopic<ICacheEvent>> CreatedTopics() =>
        _topics.Values.Where(topic => topic.IsValueCreated).Select(topic => topic.Value);

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _stopTokenSource?.Cancel();
                _stopTokenSource?.Dispose();
                DisposeTopics();
            }
        }
    }

    protected abstract ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey);

    private void DisposeTopics()
    {
        foreach (var key in _topics.Keys)
        {
            if (!_topics.TryRemove(key, out var lazyTopic) || !lazyTopic.IsValueCreated)
            {
                continue;
            }
            var topic = lazyTopic.Value;
            // Unhooked first: the removal callback would only find the topic already gone.
            topic.OnDisposed -= RemoveTopic;
            try
            {
                topic.Dispose();
            }
            catch (Exception ex)
            {
                LogTopicDisposeError(ex, key);
            }
        }
    }

    private void RemoveTopic(object? sender, EventArgs e)
    {
        if (sender is ITopic<ICacheEvent> topic)
        {
            Remove(topic.TopicKey);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error disposing topic {TopicKey}")]
    private partial void LogTopicDisposeError(Exception ex, TopicKey topicKey);
}
