namespace UiPath.Caching.PhaseB;

/// <summary>What one server of a move can do, as probed (section 3.8); each side picks script or fallback alone.</summary>
/// <param name="Replicas">Null when the server refused ROLE.</param>
/// <param name="SlotEnforced">A cross-slot two-key script is refused, so the side uses per-slot records and the tag table whatever its server type says.</param>
/// <param name="EvictionPolicy">Null when the server refused to tell.</param>
/// <param name="Denied">Commands a probe found refused (NOPERM or blocked), reported for the operator; each already shows in the capability it probed.</param>
internal sealed record SideCaps(
    Version Version,
    bool Scripts,
    bool Wait,
    ScanMode Scan,
    bool SlotEnforced,
    string? EvictionPolicy,
    int? Replicas,
    IReadOnlyList<string> Denied)
{
    public bool FallbackAllowed => Version >= MoveScripts.FallbackMinimum;

    /// <summary>Control records are never evicted: <c>noeviction</c> or a <c>volatile-*</c> policy; unknown needs the operator's attestation (G12).</summary>
    public bool? EvictionSafe => EvictionPolicy is null ? null : EvictionPolicy == "noeviction" || EvictionPolicy.StartsWith("volatile-", StringComparison.Ordinal);

    /// <summary>S3-G6 for both servers: every reason one side fails, named by side. WAIT is not required here; a side without it needs <c>Approve(barrier_unavailable_dst)</c>.</summary>
    public static IReadOnlyList<string> Gate(SideCaps source, SideCaps destination) =>
        [.. source.Failures("src"), .. destination.Failures("dst")];

    public IEnumerable<string> Failures(string side)
    {
        if (!Scripts && !FallbackAllowed)
        {
            yield return $"{side}: scripts are refused and Redis {Version} is older than {MoveScripts.FallbackMinimum}, so neither scripts nor the fallback run";
        }

        if (Scan == ScanMode.None)
        {
            yield return $"{side}: the scan probe failed";
        }

        if (EvictionSafe == false)
        {
            yield return $"{side}: maxmemory-policy {EvictionPolicy} can evict control records";
        }
    }

    /// <summary>The move scripts for this side: scripts when it runs them, the fallback otherwise.</summary>
    public MoveScripts MoveScriptsOn(StackExchange.Redis.IDatabase database) => new(database, Scripts, Version);
}
