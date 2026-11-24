using System.Net;

namespace UiPath.Caching.KeyMove;

/// <summary>A node of the cluster configuration, reduced to what naming the scan units needs.</summary>
/// <param name="NodeId">The node's id.</param>
/// <param name="EndPoint">The node's address.</param>
/// <param name="IsReplica">Whether the configuration lists the node as a replica.</param>
/// <param name="IsFail">Whether the configuration flags the node as failed.</param>
/// <param name="ParentNodeId">For a replica, the id of its primary.</param>
/// <param name="HasSlots">Whether the node serves slots.</param>
internal sealed record TopologyNode(string NodeId, EndPoint EndPoint, bool IsReplica, bool IsFail, string? ParentNodeId, bool HasSlots);
