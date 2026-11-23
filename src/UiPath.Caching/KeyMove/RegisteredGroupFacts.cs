namespace UiPath.Caching.KeyMove;

/// <summary>What the registrations say about a group; a fact no registration holds is unknown until the app attests it for that group, and an unknown fact refuses Start.</summary>
/// <param name="releaseFloorAttested">Whether configuration attests the release floor.</param>
/// <param name="formatSettled">The groups with a settled-format attestation, with the evidence id each attestation names; an id is good for one move, including its reverse.</param>
/// <param name="nodeListComplete">The groups with a complete-node-list attestation, with the evidence id each attestation names; an id is good for one move, including its reverse.</param>
/// <param name="memoryFits">The groups with a memory-headroom attestation, with the id of the dry-run report each attestation covers.</param>
/// <param name="claimWired">A real move claim is wired, which a router group needs before it may start.</param>
internal sealed class RegisteredGroupFacts(
    IReadOnlyList<KeyMoveCache> caches,
    bool releaseFloorAttested,
    IReadOnlyDictionary<string, string>? formatSettled = null,
    IReadOnlyDictionary<string, string>? nodeListComplete = null,
    IReadOnlyDictionary<string, string>? memoryFits = null,
    bool claimWired = false) : IGroupFacts
{
    public const string FormatKind = "f";
    public const string NodeKind = "n";

    public bool ReleaseFloorAttested => releaseFloorAttested;

    /// <summary>The entries of the state's consumed evidence, as the leader writes them.</summary>
    public static string Join(string existing, IReadOnlyList<string> entries) =>
        string.Join(',', existing.Split(',', StringSplitOptions.RemoveEmptyEntries).Concat(entries).Distinct(StringComparer.Ordinal));

    /// <summary>A NoOrigin cache among the managed ones makes the group take the S2 barrier.</summary>
    public bool HasNoOrigin(string group, IReadOnlyList<string> cacheIds) => Managed(group, cacheIds).Any(c => c.NoOrigin);

    public bool TakesClaim(string group) => true;

    public bool ClaimWired(string group) => claimWired;

    public string ClaimDescriptor(string group) => "rt";

    public TimeSpan? LongestSourceTtl(string group)
    {
        var all = caches.Where(c => c.Group == group).ToArray();
        return all.Length == 0 || all.Any(c => c.MaxSourceTtl is null) ? null : all.Max(c => c.MaxSourceTtl);
    }

    /// <summary>X11 needs every managed cache to accept a refill, and at least one.</summary>
    public bool AllAcceptRefill(string group, IReadOnlyList<string> cacheIds) =>
        cacheIds.Count > 0 && cacheIds.All(id => caches.Any(c => c.Group == group && c.Name == id && c.AcceptRefill));

    public bool FormatSettled(string group, StateRecord state) => Attested(formatSettled, FormatKind, group, state);

    public bool NodeListComplete(string group, StateRecord state) => Attested(nodeListComplete, NodeKind, group, state);

    /// <summary>The evidence ids a Start of the group consumes; recorded in the state, so a restart or another node cannot use them for a second move.</summary>
    public IReadOnlyList<string> StartTaken(string group) =>
    [
        .. new[] { (FormatKind, formatSettled), (NodeKind, nodeListComplete) }.Where(e => e.Item2 is not null && e.Item2.ContainsKey(group)).Select(e => Entry(e.Item1, e.Item2![group])),
    ];

    /// <summary>True only for the dry-run report the attestation of this group covers; a newer report is a different one.</summary>
    public bool MemoryFits(string group, string reportId, long estimatedBytes, double headroom) => memoryFits is not null && memoryFits.TryGetValue(group, out var covered) && covered == reportId;

    private static string Entry(string kind, string id) => kind + ":" + id;

    // Before a move the evidence must not have been consumed by one; during it (and its reverse) it must be the one that Start consumed.
    private static bool Attested(IReadOnlyDictionary<string, string>? attested, string kind, string group, StateRecord state)
    {
        if (attested is null || !attested.TryGetValue(group, out var id))
        {
            return false;
        }

        var consumed = state.Field(StateFields.Attested).Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(Entry(kind, id), StringComparer.Ordinal);
        return state.State == "S0" ? !consumed : consumed;
    }

    private IEnumerable<KeyMoveCache> Managed(string group, IReadOnlyList<string> cacheIds) =>
        caches.Where(c => c.Group == group && cacheIds.Contains(c.Name, StringComparer.Ordinal));

}
