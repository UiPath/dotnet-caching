namespace UiPath.Caching.Migration;

/// <summary>What the stored state of a key move group says, read from Redis, so any node of the application gives the same answer.</summary>
/// <param name="Group">The group.</param>
/// <param name="Stage">The stage the state is in.</param>
/// <param name="Reverse">True while the group moves back to its in-place keys.</param>
/// <param name="Durable">False while a transition to <paramref name="Stage"/> has not finished on every Redis server yet.</param>
/// <param name="OperatorHold">The reason an operator gave for a hold; null when there is none.</param>
/// <param name="AutomaticHold">The reason the move held itself (a node without key move support, or keys written after the switch); null when there is none.</param>
/// <param name="Approvals">The kinds of the approvals stored for the current state.</param>
/// <param name="Residue">Per cache, the keys the last census left on the source side, for the close or release approval to acknowledge.</param>
/// <param name="BlockingGates">Why the move's last step waited, one entry per reason as <c>code:detail</c>, where the detail names the failing gate IDs; empty once a step is taken.</param>
/// <param name="BlockedSince">When the move first waited for <paramref name="BlockingGates"/>; null when it does not wait.</param>
public sealed record KeyMoveStatus(
    string Group,
    KeyMoveStage Stage,
    bool Reverse,
    bool Durable,
    string? OperatorHold,
    string? AutomaticHold,
    IReadOnlyList<string> Approvals,
    IReadOnlyDictionary<string, long> Residue,
    IReadOnlyList<string> BlockingGates,
    DateTimeOffset? BlockedSince);
