using System.Net;

namespace UiPath.Caching.Redis;

/// <summary>Cluster membership as the client's own topology handshake last recorded it on a node.</summary>
internal interface IClusterTopologyReader
{
    /// <returns>The configuration the client last recorded on the node, a new instance each time it re-reads one; null when the node has none.</returns>
    object? GetConfiguration(IServer server);

    HashSet<EndPoint> GetMembers(object configuration);
}

/// <summary>What a topology refresh reported; when conclusive, a null <see cref="Members"/> means membership can never be judged.</summary>
internal readonly record struct ClusterMembership(bool Conclusive, HashSet<EndPoint>? Members)
{
    public static ClusterMembership Inconclusive => default;
}

[ExcludeFromCodeCoverage(Justification = "ClusterConfiguration has no public constructor; only a live cluster handshake populates it.")]
internal sealed class ClusterConfigurationReader : IClusterTopologyReader
{
    public static readonly ClusterConfigurationReader Instance = new();

    public object? GetConfiguration(IServer server) => server.ClusterConfiguration;

    public HashSet<EndPoint> GetMembers(object configuration) =>
        ((ClusterConfiguration)configuration).Nodes
            .Where(node => node.EndPoint is not null && !node.IsHandshake) // the client does not dial nodes still joining, so a rebuilt connection would not retry them
            .Select(node => node.EndPoint!)
            .ToHashSet();
}
