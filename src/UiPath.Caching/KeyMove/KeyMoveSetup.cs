namespace UiPath.Caching.KeyMove;

/// <summary>What a host gives the key move node: its groups, the stage flag, what the registrations say about the groups, and the seams that belong to other changes.</summary>
/// <param name="Groups">The groups of the node's registration, each with its flags and cache ids.</param>
/// <param name="Facts">What the registrations say about each group, for the gates.</param>
/// <param name="KeySpace">The key maps and ownership predicates, for the purge and the inventory.</param>
/// <param name="Claim">The move claim of router groups.</param>
/// <param name="LibraryPrefix">The mandatory key prefix that starts every <c>mg:</c> key without a data key.</param>
internal sealed record KeyMoveSetup(
    IReadOnlyList<GroupRegistration> Groups,
    IMigrationStageSource Stages,
    IGroupFacts Facts,
    IGroupKeySpace KeySpace,
    IMoveClaim Claim,
    string LibraryPrefix)
{
    /// <summary>The registered caches, which the router binds a codec and a key map to.</summary>
    public IReadOnlyList<KeyMoveCache> Caches { get; init; } = [];

    /// <summary>The codec's verdict on stored values, for the inventory; null counts every owned key as movable.</summary>
    public IValueClassifier? Values { get; init; }

    public KeyMoveStateOptions State { get; init; } = new();

    public StageDriverOptions Driver { get; init; } = new();

    /// <summary>Told of every alert of the node and the leader.</summary>
    public Action<KeyMoveAlert>? Alert { get; init; }

    /// <summary>Whether the mover finished a pass for <c>(group, direction)</c> after the acknowledgement of moving; null uses the pass records the mover stores, which leaves the confirming pass pending while no mover has finished a pass.</summary>
    public Func<string, string, CancellationToken, Task<bool>>? MoverFinishedPass { get; init; }

    /// <summary>Closes the data connections and opens new ones, which a node does before it acknowledges the not-started of X11; null recycles the connector's.</summary>
    public Action? RecycleConnections { get; init; }

    /// <summary>CLAIMDROP, for <c>RemoveGroup</c>; null when no group takes a claim.</summary>
    public Func<string, LeaseFence?, CancellationToken, Task<ClaimOutcome>>? DropClaim { get; init; }

    /// <summary>Overrides the retry clock and optionally wraps the topology for the router's inventory scanner in tests.</summary>
    internal (TimeProvider RetryClock, Func<IStateTopology, IStateTopology>? WrapTopology)? InventoryScanOverride { get; init; }

    internal Action<Task>? SnapshotPollScheduled { get; init; }

    internal Action<Task>? StagePollScheduled { get; init; }

    internal Action<StepOutcome?>? DriverStepped { get; init; }
}
