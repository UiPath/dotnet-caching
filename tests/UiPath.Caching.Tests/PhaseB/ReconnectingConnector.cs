using System.Net;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.PhaseB;

/// <summary>A connector whose multiplexer can be replaced, also at the moment a handler subscribes to its reconnect event.</summary>
internal sealed class ReconnectingConnector : IRedisConnector
{
    private readonly IDatabase _database;
    private EventCountingMultiplexer _current;

    public ReconnectingConnector(EventCountingMultiplexer current)
    {
        _current = current;
        _database = InterceptingProxy.Wrap<IDatabase>(null!, (method, _) => method.Name == "get_Multiplexer" ? _current.Multiplexer : throw new NotSupportedException(method.Name));
    }

    public event EventHandler? OnConnectionFailed
    {
        add { /* unused */ }
        remove { /* unused */ }
    }

    public event EventHandler? OnConnectionRestored
    {
        add { /* unused */ }
        remove { /* unused */ }
    }

    public event EventHandler? OnReconnected
    {
        add
        {
            if (ReplaceOnSubscribe is { } replacement)
            {
                _current = replacement;
            }

            Handler += value;
        }

        remove => Handler -= value;
    }

    /// <summary>The multiplexer that takes over when a handler subscribes, as a reconnect that lands between a capture and a subscription.</summary>
    public EventCountingMultiplexer? ReplaceOnSubscribe { get; init; }

    public bool IsConnected => true;

    public Version Version => new(7, 4);

    public IDatabase Database => _database;

    public ISubscriber Subscriber => throw new NotSupportedException();

    private EventHandler? Handler { get; set; }

    public void Reconnect(EventCountingMultiplexer replacement)
    {
        _current = replacement;
        Handler?.Invoke(this, EventArgs.Empty);
    }

    public void ForceReconnect() => throw new NotSupportedException();

    public EndPoint[] GetEndPoints(bool configuredOnly = false) => [];

    public void Dispose()
    {
    }
}
