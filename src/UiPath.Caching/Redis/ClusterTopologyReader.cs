using System.Net;

namespace UiPath.Caching.Redis;

/// <summary>Cluster membership as the client's own topology handshake last recorded it on a node.</summary>
internal interface IClusterTopologyReader
{
    /// <returns>The configuration the client last recorded on the node, a new instance each time it re-reads one; null when the node has none.</returns>
    object? GetConfiguration(IServer server);

    HashSet<EndPoint> GetMembers(object configuration);
}
