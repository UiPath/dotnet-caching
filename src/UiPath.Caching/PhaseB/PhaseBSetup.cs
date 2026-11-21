namespace UiPath.Caching.PhaseB;

/// <summary>What a host gives the Phase B node: its groups, the stage flag, what the registrations say about the groups, and the seams that belong to other changes.</summary>
/// <param name="Groups">The groups of the node's registration, each with its flags and cache ids.</param>
/// <param name="Stages">The stage flag F15.</param>
/// <param name="Facts">What the registrations say about each group, for the gates.</param>
/// <param name="KeySpace">The key maps and ownership predicates, for the purge and the census.</param>
/// <param name="Claim">The move claim of router groups.</param>
/// <param name="LibraryPrefix">The mandatory key prefix that starts every <c>mg:</c> key without a data key.</param>
internal sealed record PhaseBSetup(
    IReadOnlyList<GroupRegistration> Groups,
    IMigrationStageSource Stages,
    IGroupFacts Facts,
    IGroupKeySpace KeySpace,
    IMoveClaim Claim,
    string LibraryPrefix)
{
    public PhaseBStateOptions State { get; init; } = new();

    public StageDriverOptions Driver { get; init; } = new();

    /// <summary>Told as <c>(group, code, detail)</c> of every alert of the node and the leader.</summary>
    public Action<string, string, string>? Alert { get; init; }

    /// <summary>Whether the mover finished a pass for <c>(group, direction)</c>; null when there is no mover, which keeps D1 from ever passing.</summary>
    public Func<string, string, CancellationToken, Task<bool>>? MoverFinishedPass { get; init; }

    /// <summary>Closes the data connections and opens new ones, which a node does before it acknowledges the S0 of X11; null recycles the connector's.</summary>
    public Action? RecycleConnections { get; init; }

    /// <summary>CLAIMDROP, for <c>RemoveGroup</c>; null when no group takes a claim.</summary>
    public Func<string, LeaseFence?, CancellationToken, Task<ClaimOutcome>>? DropClaim { get; init; }
}
