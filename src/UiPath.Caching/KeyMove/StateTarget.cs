namespace UiPath.Caching.KeyMove;

/// <summary>What TRANSIT writes: the target state with its new identity and the last committed state; the optional fields are written only when given.</summary>
/// <param name="Prev">The last committed state as <c>st:dir:nPop:ep:id</c>.</param>
/// <param name="MoveId">The <c>ep</c> of an X1, X7 or X13 transition, which nothing else writes.</param>
/// <param name="Claim">The claim token of an X1 for a group that takes a move claim.</param>
/// <param name="Caches">The encoded managed set and its hash, for a create-if-absent write or a rebuild that changes the set.</param>
/// <param name="Move">The move descriptor <c>mv</c> of a group that takes a move claim.</param>
/// <param name="Release">The claim certificate copy <c>rel</c>, for a recreation.</param>
/// <param name="HoldOp">The reason of an operator hold written with the state, as a recreation and <c>Reassert</c> do.</param>
/// <param name="Clear">Fields deleted in the same step: what a rebuilt state must not inherit.</param>
/// <param name="Set">Fields written in the same step, after the deletions.</param>
internal sealed record StateTarget(
    string State,
    string Direction,
    int NPop,
    long Ep,
    string Id,
    string Prev,
    long UpdatedAt,
    long? MoveId = null,
    string? Claim = null,
    (string Caches, string CacheSet)? Caches = null,
    string? Move = null,
    string? Release = null,
    string? HoldOp = null,
    IReadOnlyList<string>? Clear = null,
    IReadOnlyList<(string Field, string Value)>? Set = null);
