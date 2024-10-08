using System.Collections.Concurrent;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public abstract class RedisTopicProviderBase : ITopicProvider, IConnectionState
{
    private bool _disposed;

    private readonly ConcurrentDictionary<TopicKey, Lazy<ITopic<ICacheEvent>>> _topics = new();

    private readonly CancellationTokenSource _stopTokenSource = new();

    private readonly IConnectionState _connectionState;

    protected RedisTopicProviderBase(IRedisConnector redis, ICachingTelemetryProvider telemetryProvider, bool connectionMonitorEnabled)
    {
        Redis = redis;
        _connectionState = connectionMonitorEnabled ? new ConnectionStateMonitor(telemetryProvider, redis) : NullConnectionStateMonitor.Instance;
    }

    protected IRedisConnector Redis { get; }


    public event EventHandler? OnConnectionFailed
    {
        add => _connectionState.OnConnectionFailed += value;
        remove => _connectionState.OnConnectionFailed -= value;
    }

    public event EventHandler? OnConnectionRestored
    {
        add => _connectionState.OnConnectionRestored += value;
        remove => _connectionState.OnConnectionRestored -= value;
    }

    public event EventHandler? OnReconnected
    {
        add => _connectionState.OnReconnected += value;
        remove => _connectionState.OnReconnected -= value;
    }

    public bool IsConnected => _connectionState.IsConnected;

    public abstract string Name { get; }

    public abstract bool Enabled { get; }

    public ICollection<TopicKey> Keys => _topics.Keys;

    public ITopic<ICacheEvent> Create(TopicKey topicKey) =>
        _topics.GetOrAdd(topicKey, tk => new Lazy<ITopic<ICacheEvent>>(() => {
            var t = CreateInternalTopic(tk);
            t.OnDisposed += RemoveTopic;
            return t;
        })).Value;

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

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _connectionState.Dispose();
                _stopTokenSource?.Cancel();
                _stopTokenSource?.Dispose();
                _topics.Clear();
            }
            _disposed = true;
        }
    }

    private void RemoveTopic(object? sender, EventArgs e)
    {
        if (sender is ITopic<ICacheEvent> topic)
        {
            Remove(topic.TopicKey);
        }
    }

    protected abstract ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey);
}
