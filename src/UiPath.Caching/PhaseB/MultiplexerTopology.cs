using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The topology read from the application's connection; each master gets a separate connection built from the same configuration.</summary>
internal sealed class MultiplexerTopology(
    IConnectionMultiplexer application,
    string connectorPrefix,
    bool useScripts,
    Version? serverVersion,
    int? expectedReplicasPerMaster = null,
    Func<ConfigurationOptions, Task<IConnectionMultiplexer>>? connect = null) : IStateTopology
{
    public async Task<ClusterLayout> ReadLayoutAsync(CancellationToken cancellationToken)
    {
        var servers = application.GetServers().Where(s => s.IsConnected).ToArray();
        if (servers.Length == 0)
        {
            throw new RedisException("No server of the connection is connected.");
        }

        if (servers[0].ServerType != ServerType.Cluster)
        {
            var master = servers.FirstOrDefault(s => !s.IsReplica) ?? servers[0];
            var replicas = expectedReplicasPerMaster ?? await ConnectedReplicasAsync(master).WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ClusterLayout([new MasterLayout(master.EndPoint, [new SlotRange(0, HashSlot.Count - 1)], replicas)]);
        }

        var nodes = (await ClusterConfigurationAsync(servers, cancellationToken).ConfigureAwait(false)).Nodes;
        var masters = nodes
            .Where(n => !n.IsReplica && n.Slots.Count > 0)
            .Select(n => new MasterLayout(n.EndPoint!, [.. n.Slots], expectedReplicasPerMaster ?? nodes.Count(r => r.ParentNodeId == n.NodeId)))
            .ToArray();
        return new ClusterLayout(masters);
    }

    public async Task<IMasterChannel> OpenAsync(EndPoint master, CancellationToken cancellationToken)
    {
        var options = ConfigurationOptions.Parse(application.Configuration);
        options.EndPoints.Clear();
        options.EndPoints.Add(master);
        var mux = connect is null
            ? await ConnectionMultiplexer.ConnectAsync(options).WaitAsync(cancellationToken).ConfigureAwait(false)
            : await connect(options).WaitAsync(cancellationToken).ConfigureAwait(false);
        return new MasterChannel(mux, master, connectorPrefix, useScripts, serverVersion);
    }

    // CLUSTER NODES needs an admin connection; without one the configuration the connection already holds is used, which it refreshes when it meets a MOVED.
    private static async Task<ClusterConfiguration> ClusterConfigurationAsync(IServer[] servers, CancellationToken cancellationToken)
    {
        try
        {
            return (await servers[0].ClusterNodesAsync().WaitAsync(cancellationToken).ConfigureAwait(false))!;
        }
        catch (RedisCommandException)
        {
            return servers.Select(s => s.ClusterConfiguration).FirstOrDefault(c => c is not null)
                ?? throw new RedisException("The connection holds no cluster configuration.");
        }
    }

    // ROLE, unlike INFO, needs no admin connection: [master, offset, [[host, port, offset], ...]].
    private static async Task<int> ConnectedReplicasAsync(IServer master)
    {
        var role = (RedisResult[])(await master.ExecuteAsync("ROLE").ConfigureAwait(false))!;
        return role.Length > 2 && (string?)role[0] == "master" ? ((RedisResult[])role[2]!).Length : 0;
    }
}
