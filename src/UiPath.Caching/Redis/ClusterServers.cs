using System.Net;

namespace UiPath.Caching.Redis;

/// <summary>
/// Narrows the servers of a multiplexer to the nodes of a cluster. A cluster reached through an entry endpoint (a DNS name or configuration endpoint of a
/// managed service) lists that endpoint as a server of its own beside the node it resolves to, so a walk over every server visits that node twice.
/// </summary>
internal static class ClusterServers
{
    /// <summary>
    /// The servers that are nodes of the cluster they say they belong to. A cluster server whose endpoint is not among the nodes the connected servers' own cluster
    /// configuration lists is an entry endpoint and is dropped. Servers pass through unchanged when none carries a cluster configuration, and the input is returned
    /// when the filter would leave nothing.
    /// </summary>
    public static IReadOnlyList<IServer> Nodes(IEnumerable<IServer> servers, IClusterTopologyReader? reader = null)
    {
        var all = servers as IReadOnlyList<IServer> ?? [.. servers];
        reader ??= ClusterConfigurationReader.Instance;
        HashSet<EndPoint>? members = null;
        foreach (var server in all)
        {
            if (server.ServerType == ServerType.Cluster && server.IsConnected && reader.GetConfiguration(server) is { } configuration)
            {
                (members ??= []).UnionWith(reader.GetMembers(configuration));
            }
        }

        if (members is null || members.Count == 0)
        {
            return all;
        }

        // EndPoint equality is the client's own: the nodes come from the cluster's answer, as IP endpoints, and the entry endpoint is a DNS one, so it never matches.
        var nodes = all.Where(server => server.ServerType != ServerType.Cluster || members.Contains(server.EndPoint)).ToList();
        return nodes.Count == 0 ? all : nodes;
    }
}
