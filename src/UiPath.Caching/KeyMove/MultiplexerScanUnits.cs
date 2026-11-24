using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>The units of a connection: each primary with slots on a cluster (by node id), or the one standalone server (by endpoint).</summary>
internal sealed class MultiplexerScanUnits(Func<IConnectionMultiplexer> multiplexer) : IScanUnits
{
    public Task<IReadOnlyList<ScanUnit>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var all = multiplexer().GetServers();
        var servers = all.Where(s => s.IsConnected).ToArray();
        if (servers.Length == 0)
        {
            throw new RedisException("No server of the connection is connected.");
        }

        if (servers[0].ServerType != ServerType.Cluster)
        {
            return Task.FromResult(Standalone(all));
        }

        var nodes = servers.Select(s => s.ClusterConfiguration).FirstOrDefault(c => c is not null)?.Nodes
            ?? throw new RedisException("The connection holds no cluster configuration, so the scan units cannot be named.");
        return Task.FromResult(Cluster(servers, [.. nodes.Select(n => new TopologyNode(n.NodeId, n.EndPoint!, n.IsReplica, n.IsFail, n.ParentNodeId, n.Slots.Count > 0))]));
    }

    /// <summary>The one unit of a standalone deployment; the replicas it lists include one that is down.</summary>
    internal static IReadOnlyList<ScanUnit> Standalone(IReadOnlyList<IServer> all)
    {
        var connected = all.Where(s => s.IsConnected).ToArray();
        var primary = connected.FirstOrDefault(s => !s.IsReplica) ?? connected[0];
        var replicas = all.Where(s => s.IsReplica).Select(s => new ScanReplica(Text(s.EndPoint), s.IsConnected ? s : null)).ToArray();
        return [new ScanUnit(Text(primary.EndPoint), primary, connected.FirstOrDefault(s => s.IsReplica)) { Replicas = replicas }];
    }

    /// <summary>The units of a cluster, each with every replica the configuration names; an unreachable or failed one has no server.</summary>
    internal static IReadOnlyList<ScanUnit> Cluster(IReadOnlyList<IServer> connected, IReadOnlyList<TopologyNode> nodes)
    {
        var units = new List<ScanUnit>();
        foreach (var node in nodes.Where(n => !n.IsReplica && n.HasSlots))
        {
            var primary = connected.FirstOrDefault(s => EndPointMatch.Same(s.EndPoint, node.EndPoint))
                ?? throw new RedisException($"The master {node.EndPoint} is not connected, so its keys cannot be scanned.");
            var replica = nodes.Where(n => n.IsReplica && !n.IsFail && n.ParentNodeId == node.NodeId)
                .Select(n => connected.FirstOrDefault(s => EndPointMatch.Same(s.EndPoint, n.EndPoint)))
                .FirstOrDefault(s => s is not null);

            // A replica the configuration names but the connection cannot reach, or one flagged failed, stays in the list so the health reads it as down.
            var known = nodes.Where(n => n.IsReplica && n.ParentNodeId == node.NodeId)
                .Select(n => new ScanReplica(n.NodeId, n.IsFail ? null : connected.FirstOrDefault(s => EndPointMatch.Same(s.EndPoint, n.EndPoint))))
                .ToArray();
            units.Add(new ScanUnit(node.NodeId, primary, replica) { Replicas = known });
        }

        return units;
    }

    private static string Text(EndPoint endpoint) => endpoint switch
    {
        IPEndPoint ip => $"{ip.Address}:{ip.Port}",
        DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
        _ => endpoint.ToString() ?? string.Empty,
    };
}
