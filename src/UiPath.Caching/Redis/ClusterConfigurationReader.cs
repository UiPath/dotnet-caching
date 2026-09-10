using System.Net;

namespace UiPath.Caching.Redis;

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
