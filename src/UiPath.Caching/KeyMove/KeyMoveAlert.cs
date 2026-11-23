namespace UiPath.Caching.KeyMove;

/// <summary>An alert of the key move node or its leader.</summary>
/// <remarks>
/// Codes raised by the registration and the data operations: <c>ambiguous_ownership</c> (a key two caches own, left in place),
/// <c>unmovable_destination</c> (a cache maps a key outside its own namespace), <c>scan_dedupe_cap</c> (overlapping scan patterns matched too many keys),
/// <c>rewire_failed</c>, <c>gate_stuck</c>, <c>gate_reopened</c> and <c>upkeep_failed</c>. The leader's own codes follow the design document.
/// </remarks>
public sealed class KeyMoveAlert
{
    internal KeyMoveAlert(string group, string code, string detail)
    {
        Group = group;
        Code = code;
        Detail = detail;
    }

    /// <summary>The group it concerns; empty for the node.</summary>
    public string Group { get; }

    /// <summary>What happened.</summary>
    public string Code { get; }

    /// <summary>What the code names, such as a cache, a key or a node.</summary>
    public string Detail { get; }
}
