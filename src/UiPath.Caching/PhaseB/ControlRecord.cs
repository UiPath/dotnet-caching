namespace UiPath.Caching.PhaseB;

/// <summary>What one transition writes into a control record: the target identity and state, the previous committed one, and a fresh open gate per cache.</summary>
internal sealed record ControlRecord(
    long Ep,
    string Id,
    string State,
    string Direction,
    string NPop,
    string PreviousState,
    string PreviousDirection,
    string PreviousNPop,
    string PreviousEp,
    string PreviousId,
    IReadOnlyList<(string Cid, string Nonce)> Gates);
