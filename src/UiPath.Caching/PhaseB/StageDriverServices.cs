namespace UiPath.Caching.PhaseB;

/// <summary>The collaborators of the leader's step loop.</summary>
/// <param name="HoldsLease">True while this node holds the group's leader lease; a node that lost it writes nothing.</param>
/// <param name="Monitor">The node's own snapshot loop, which reconciles the control records; null for none.</param>
/// <param name="Fence">The lease fence of a group, carried by every leader mutation that accepts one, so the server refuses a leader whose lease was taken over.</param>
/// <param name="OwnScope">The scope of this node's adverts (G4).</param>
/// <param name="Alert">Told of every alert the first time it appears and again after <c>RepeatAlertAfter</c>, as <c>(group, code, detail)</c>.</param>
internal sealed record StageDriverServices(
    StateStore Store,
    MoveScripts Scripts,
    MoveKeyNames Names,
    TransitionSequence Sequence,
    PreflightChecker Preflight,
    BarrierLedger Ledger,
    IAdvertSource Adverts,
    IGroupFacts Facts,
    IMoveClaim Claim,
    IGroupDataOps Data,
    IMoverStatus Mover,
    Func<string, bool> HoldsLease,
    GroupStateMonitor? Monitor = null,
    string? OwnScope = null,
    Action<string, string, string>? Alert = null,
    Func<string, LeaseFence?>? Fence = null);
