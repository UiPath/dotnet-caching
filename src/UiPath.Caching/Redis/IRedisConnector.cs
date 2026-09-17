using System.Net;

namespace UiPath.Caching.Redis;

public interface IRedisConnector : IConnectionState, IDisposable
{
    Version Version { get; }

    IDatabase Database { get; }

    ISubscriber Subscriber { get; }

    void ForceReconnect();

    EndPoint[] GetEndPoints(bool configuredOnly = false);

    /// <summary>
    /// The connected primaries, for a command a server answers out of its own keyspace rather than one the
    /// client can route by key -- <c>SCAN</c> above all. Empty while the connection is not yet established;
    /// an implementation that does not override it leaves such a command on <see cref="Database"/>, which
    /// reaches only whichever single server the multiplexer routes it to.
    /// </summary>
    IEnumerable<IServer> GetPrimaries() => [];

    /// <summary>Optionally pre-establishes the connection on a fully async path.</summary>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
