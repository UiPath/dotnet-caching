namespace UiPath.Caching.Migration;

/// <summary>What the pin keeps in Redis: the switch state of each group, and the node hash it reads the acknowledgements from. Every call may throw.</summary>
internal interface IPinStore
{
    Task<SwitchRead> ReadStateAsync(string group, CancellationToken cancellationToken);

    Task MarkSwitchedAsync(string group, CancellationToken cancellationToken);

    Task<IReadOnlyList<NodeEntry>> ReadNodesAsync(CancellationToken cancellationToken);
}
