namespace UiPath.Caching.PhaseB;

/// <summary>What one side of a move can do, as probed on each of its masters (section 3.8); each side picks script or fallback alone.</summary>
/// <param name="Masters">Every primary the side's view reaches, each probed alone: one older or restricted master decides for the side.</param>
/// <param name="SlotEnforced">A cross-slot two-key script is refused, so the side uses per-slot records and the tag table whatever its server type says.</param>
/// <param name="Denied">Commands a probe found refused (NOPERM or blocked), reported for the operator; each already shows in the capability it probed.</param>
internal sealed record SideCaps(
    IReadOnlyList<MasterCaps> Masters,
    ScanMode Scan,
    bool SlotEnforced,
    IReadOnlyList<string> Denied)
{
    /// <summary>The oldest master's version.</summary>
    public Version Version => Masters.Min(static m => m.Version)!;

    /// <summary>Every master runs scripts.</summary>
    public bool Scripts => Masters.All(static m => m.Scripts);

    /// <summary>Every master answered a WAIT.</summary>
    public bool Wait => Masters.All(static m => m.Wait);

    /// <summary>The fewest replicas of any master; null when one refused ROLE.</summary>
    public int? Replicas => Masters.Any(static m => m.Replicas is null) ? null : Masters.Min(static m => m.Replicas);

    /// <summary>The first policy that can evict control records; else null when any master did not tell; else the shared value.</summary>
    public string? EvictionPolicy =>
        Masters.FirstOrDefault(static m => m.EvictionSafe == false)?.EvictionPolicy
        ?? (Masters.Any(static m => m.EvictionPolicy is null) ? null : Masters.FirstOrDefault()?.EvictionPolicy);

    /// <summary>S3-G6 for both servers: every reason one side fails, named by side. WAIT is not required here; a side without it needs <c>Approve(barrier_unavailable_dst)</c>.</summary>
    public static IReadOnlyList<string> Gate(SideCaps source, SideCaps destination) =>
        [.. source.Failures("src"), .. destination.Failures("dst")];

    public IEnumerable<string> Failures(string side)
    {
        foreach (var master in Masters)
        {
            foreach (var failure in master.Failures(side))
            {
                yield return failure;
            }
        }

        if (Scan == ScanMode.None)
        {
            yield return $"{side}: the scan probe failed";
        }
    }

    /// <summary>The move scripts for this side: scripts when every master runs them, the fallback otherwise.</summary>
    public MoveScripts MoveScriptsOn(StackExchange.Redis.IDatabase database) => new(database, Scripts, Version);
}
