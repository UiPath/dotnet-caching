namespace UiPath.Caching.KeyMove;

/// <param name="HoldsLease">True while this node holds the group's leader lease; a node that lost it writes nothing.</param>
/// <param name="Fence">The lease fence of a group, carried by every leader mutation that accepts one, so the server refuses a leader whose lease was taken over.</param>
/// <param name="Upkeep">Rebuilds control records when the managed cache set changes.</param>
/// <param name="Retire">The retire gate; without it the reverse never leaves returning and waits for the operator's consent.</param>
/// <param name="Sweeper">Runs the retire sweep of the markers of the retiring generation in returning.</param>
/// <param name="Alert">Told of every alert the first time it appears and again after <c>RepeatAlertAfter</c>.</param>
internal sealed record StageDriverServices(
    StateStore Store,
    MoveScripts Scripts,
    MoveKeyNames Names,
    ITransitionSequence Sequence,
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
    Action<KeyMoveAlert>? Alert = null,
    Func<string, LeaseFence?>? Fence = null,
    ControlUpkeep? Upkeep = null,
    RetireGate? Retire = null,
    MarkerSweeper? Sweeper = null);
