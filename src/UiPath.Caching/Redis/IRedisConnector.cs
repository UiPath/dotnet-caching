using System.Net;
using StackExchange.Redis.Maintenance;

namespace UiPath.Caching.Redis;

public interface IRedisConnector : IConnectionState, IDisposable
{
    /// <summary>Maintenance the server announced on the connection carrying commands.</summary>
    event EventHandler<ServerMaintenanceEvent>? ServerMaintenance
    {
        // Defaulted to never raising, so an existing implementer neither breaks nor starts forwarding.
        add => _ = value;
        remove => _ = value;
    }

    /// <summary>The server version as the server reports it.</summary>
    /// <remarks>Redis-compatible servers may misstate it, so callers treat it as a hint and fall back when a command it gates is refused.</remarks>
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
