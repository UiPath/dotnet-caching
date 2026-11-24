using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>The units of a connection: each primary with slots on a cluster, identified by its node id, or the one server of a standalone deployment, identified by its endpoint.</summary>
internal sealed class MultiplexerScanUnits(Func<IConnectionMultiplexer> multiplexer) : IScanUnits
{
    public Task<IReadOnlyList<ScanUnit>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var servers = multiplexer().GetServers().Where(s => s.IsConnected).ToArray();
        if (servers.Length == 0)
        {
            throw new RedisException("No server of the connection is connected.");
        }

        if (servers[0].ServerType != ServerType.Cluster)
        {
            var primary = servers.FirstOrDefault(s => !s.IsReplica) ?? servers[0];
            return Task.FromResult<IReadOnlyList<ScanUnit>>([new ScanUnit(Text(primary.EndPoint), primary, servers.FirstOrDefault(s => s.IsReplica))]);
        }

        var nodes = servers.Select(s => s.ClusterConfiguration).FirstOrDefault(c => c is not null)?.Nodes.ToArray()
            ?? throw new RedisException("The connection holds no cluster configuration, so the scan units cannot be named.");
        var units = new List<ScanUnit>();
        foreach (var node in nodes.Where(n => !n.IsReplica && n.Slots.Count > 0))
        {
            var primary = servers.FirstOrDefault(s => EndPointMatch.Same(s.EndPoint, node.EndPoint))
                ?? throw new RedisException($"The master {node.EndPoint} is not connected, so its keys cannot be scanned.");
            var replica = nodes.Where(n => n.IsReplica && !n.IsFail && n.ParentNodeId == node.NodeId)
                .Select(n => servers.FirstOrDefault(s => EndPointMatch.Same(s.EndPoint, n.EndPoint)))
                .FirstOrDefault(s => s is not null);
            units.Add(new ScanUnit(node.NodeId, primary, replica));
        }

        return Task.FromResult<IReadOnlyList<ScanUnit>>(units);
    }

    private static string Text(EndPoint endpoint) => endpoint switch
    {
        IPEndPoint ip => $"{ip.Address}:{ip.Port}",
        DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
        _ => endpoint.ToString() ?? string.Empty,
    };
}
