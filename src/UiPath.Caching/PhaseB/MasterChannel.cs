using System.Net;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

namespace UiPath.Caching.PhaseB;

internal sealed class MasterChannel : IMasterChannel
{
    private readonly IConnectionMultiplexer _mux;

    public MasterChannel(IConnectionMultiplexer mux, EndPoint endpoint, string connectorPrefix, bool useScripts, Version? serverVersion)
    {
        _mux = mux;
        Endpoint = endpoint;
        var database = mux.GetDatabase();
        ControlDatabase = connectorPrefix.Length == 0 ? database : database.WithKeyPrefix(connectorPrefix);
        Control = new MoveScripts(ControlDatabase, useScripts, serverVersion);
        StateDatabase = database;
        State = new MoveScripts(database, useScripts, serverVersion);
    }

    public EndPoint Endpoint { get; }

    public MoveScripts Control { get; }

    public IDatabase ControlDatabase { get; }

    public MoveScripts State { get; }

    public IDatabase StateDatabase { get; }

    public async Task<long> WaitAsync(int replicas, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var reply = await _mux.GetServer(Endpoint).ExecuteAsync("WAIT", replicas, (long)timeout.TotalMilliseconds).WaitAsync(cancellationToken).ConfigureAwait(false);
        return (long)reply;
    }

    public async ValueTask DisposeAsync() => await _mux.DisposeAsync().ConfigureAwait(false);
}
