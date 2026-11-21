using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>A connection of its own to one master: the writes and the <c>WAIT</c> that confirms them travel on it, never on the application's connection.</summary>
internal interface IMasterChannel : IAsyncDisposable
{
    EndPoint Endpoint { get; }

    /// <summary>The scripts over the view that names control records (with the connector prefix).</summary>
    MoveScripts Control { get; }

    IDatabase ControlDatabase { get; }

    /// <summary>The scripts over the view that names the state hash (no connector prefix).</summary>
    MoveScripts State { get; }

    IDatabase StateDatabase { get; }

    /// <summary>The master this connection would send a command on <paramref name="key"/> to, null when it does not know.</summary>
    Task<EndPoint?> ControlOwnerAsync(RedisKey key);

    Task<EndPoint?> StateOwnerAsync(RedisKey key);

    /// <returns>The number of replicas that acknowledged this connection's writes, at most the timeout later.</returns>
    Task<long> WaitAsync(int replicas, TimeSpan timeout, CancellationToken cancellationToken);
}
