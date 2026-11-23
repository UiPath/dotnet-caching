namespace UiPath.Caching.Migration;

/// <summary>What the pin keeps in Redis; every call may throw. A record is <c>{incarnation}:{generation}</c>, odd for an acknowledgement and even for a withdrawal.</summary>
internal interface IPinStore
{
    Task<SwitchRead> ReadStateAsync(string group, CancellationToken cancellationToken);

    /// <summary>Writes a switch that was already committed, when its key is gone. It checks nothing, so only a node that saw the switch may call it.</summary>
    Task RestoreSwitchAsync(string group, CancellationToken cancellationToken);

    Task<IReadOnlyList<NodeEntry>> ReadNodesAsync(CancellationToken cancellationToken);

    /// <summary>Records this node's acknowledgement or withdrawal and reads the switch state in the same step; a record of a later generation of the same incarnation is kept.</summary>
    Task<AckPublish> PublishAckAsync(string group, string field, string incarnation, long generation, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> ReadAcksAsync(string group, CancellationToken cancellationToken);

    /// <summary>Records the switch only while every given node's acknowledgement record is exactly the value given; false when any changed.</summary>
    Task<bool> CommitSwitchAsync(string group, IReadOnlyList<KeyValuePair<string, string>> acknowledgements, CancellationToken cancellationToken);
}
