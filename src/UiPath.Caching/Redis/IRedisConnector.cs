using System.Net;

namespace UiPath.Caching.Redis;

public interface IRedisConnector : IConnectionState, IDisposable
{
    Version Version { get; }

    IDatabase Database { get; }

    ISubscriber Subscriber { get; }

    void ForceReconnect();

    EndPoint[] GetEndPoints(bool configuredOnly = false);

    /// <summary>The connected primaries, for a server-scoped command such as <c>SCAN</c>.</summary>
    IEnumerable<IServer> GetPrimaries() => [];

    /// <summary>Optionally pre-establishes the connection on a fully async path.</summary>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
