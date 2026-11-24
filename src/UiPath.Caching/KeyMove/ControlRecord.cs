namespace UiPath.Caching.KeyMove;

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
    IReadOnlyList<(string Cid, string Nonce)> Gates)
{
    /// <summary>The fields every record holds whatever its gates: the 11 of the state, the cache list <c>cl</c>, the limit <c>lim</c> and the peak <c>pk</c>.</summary>
    public const int FixedFields = 14;

    /// <summary>The ceiling on the record's field count that a first marked write is judged against; null leaves the record's own.</summary>
    public long? Limit { get; init; }

    /// <summary>The record field that holds the marker binding value, or empty for the identity; the next three are copied as they are given, and empty removes them.</summary>
    public string BindingField { get; init; } = string.Empty;

    public string ForwardGeneration { get; init; } = string.Empty;

    public string RetiringGeneration { get; init; } = string.Empty;
}
