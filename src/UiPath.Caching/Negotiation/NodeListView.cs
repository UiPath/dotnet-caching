namespace UiPath.Caching.Negotiation;

/// <summary>What one node list read found: the advertisements of the nodes that read, the live nodes without one, and whether the node list can see every node.</summary>
internal sealed record NodeListView(IReadOnlyList<StoredAdvertisement> Ads, IReadOnlyList<string> Blockers, bool Complete)
{
    /// <summary>Each reading node and the process it advertised from, so a node that joins or restarts is told from one that merely stays.</summary>
    public IReadOnlyCollection<string> Readers { get; } = [.. Ads.Select(a => a.NodeId + "/" + a.Advertisement.Instance)];
}
