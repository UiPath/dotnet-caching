using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>A connection of its own to one master: the writes and the <c>WAIT</c> that confirms them travel on it, never on the application's connection.</summary>
internal interface IMasterChannel : IAsyncDisposable
{
    EndPoint Endpoint { get; }

    MoveScripts Control { get; }

    IDatabase ControlDatabase { get; }

    MoveScripts State { get; }

    IDatabase StateDatabase { get; }

    Task<EndPoint?> ControlOwnerAsync(RedisKey key);

    Task<EndPoint?> StateOwnerAsync(RedisKey key);

    /// <returns>The number of replicas that acknowledged this connection's writes, at most the timeout later.</returns>
    Task<long> WaitAsync(int replicas, TimeSpan timeout, CancellationToken cancellationToken);
}
