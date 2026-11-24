namespace UiPath.Caching.Migration;

/// <summary>What the mover reads of a node from two readings of <c>INFO</c>.</summary>
/// <param name="Unit">The scan unit.</param>
/// <param name="IsReplica">Whether the node is a replica.</param>
/// <param name="CpuPercent">Percent of one core used since the last reading; null at the first.</param>
/// <param name="LagBytes">For a replica, the bytes it is behind its primary; null when either offset is unknown.</param>
/// <param name="LinkDown">For a replica, whether its link to the primary is down.</param>
internal sealed record NodeLoad(string Unit, bool IsReplica, double? CpuPercent, long? LagBytes, bool LinkDown);
