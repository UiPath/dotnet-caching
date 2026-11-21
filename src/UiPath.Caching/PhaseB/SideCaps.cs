using System.Net;

namespace UiPath.Caching.PhaseB;

/// <summary>What one side of a move can do, as probed on each of its masters (section 3.8); each side picks script or fallback alone.</summary>
/// <param name="Masters">Every primary the side's view reaches, each probed alone: one older or restricted master decides for the side.</param>
/// <param name="SlotEnforced">A cross-slot two-key script is refused, so the side uses per-slot records and the tag table whatever its server type says.</param>
/// <param name="Denied">Commands a probe found refused (NOPERM or blocked), each as <c>command@endpoint</c>, reported for the operator; each already shows in the capability it probed.</param>
/// <param name="Unreachable">Primaries the view lists that were not connected; each is a failure, never dropped from the probe or the scan.</param>
internal sealed record SideCaps(
    IReadOnlyList<MasterCaps> Masters,
    ScanMode Scan,
    bool SlotEnforced,
    IReadOnlyList<string> Denied,
    IReadOnlyList<EndPoint> Unreachable)
{
    /// <summary>Primaries whose probe keys could not be deleted; the keys carry an expiry and go on their own, which <see cref="Notes"/> reports.</summary>
    public IReadOnlyList<EndPoint> Uncleaned { get; init; } = [];

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

    /// <summary>S3-G6 for both servers: every reason one side fails, named by side.</summary>
    /// <remarks>WAIT is not required here; a side without it needs <c>Approve(barrier_unavailable_dst)</c>.</remarks>
    public static IReadOnlyList<string> Gate(SideCaps source, SideCaps destination) =>
        [.. source.Failures("src"), .. destination.Failures("dst")];

    /// <summary>Every reason this side fails the gate, named by side.</summary>
    /// <remarks>A side that does not use scripts (any master lacks them) takes the fallback on every master, so each needs it, not only the one without scripts.</remarks>
    public IEnumerable<string> Failures(string side)
    {
        foreach (var endpoint in Unreachable)
        {
            yield return $"{side}: {endpoint} unreachable";
        }

        foreach (var master in Masters)
        {
            foreach (var failure in master.Failures(side))
            {
                yield return failure;
            }

            if (!Scripts && master.Scripts && !(master.FallbackAllowed && master.Transactions))
            {
                yield return $"{side}: {master.EndPoint} cannot run the fallback (watched transactions on Redis {MoveScripts.FallbackMinimum} or later), and another master of the side refuses scripts, so the side needs it";
            }
        }

        if (Masters.Any(static m => m.NeedsAdmin))
        {
            yield return $"{side}: the move connection needs allowAdmin";
        }

        if (Scan == ScanMode.None)
        {
            yield return $"{side}: the scan probe failed";
        }
    }

    /// <summary>What the operator should know about this side without it failing the gate.</summary>
    public IEnumerable<string> Notes(string side)
    {
        if (Scan == ScanMode.Incomplete)
        {
            yield return $"{side}: scan probe incomplete; the move's first scan traverses fully";
        }

        foreach (var endpoint in Uncleaned)
        {
            yield return $"{side}: {endpoint} could not delete its probe keys; they expire on their own within a minute";
        }

        if (Masters.Any(static m => m.EvictionPolicy is null))
        {
            yield return $"{side}: eviction policy unknown, needs attestation (G12)";
        }
    }

    /// <summary>The move scripts for this side: scripts when every master runs them, the fallback otherwise.</summary>
    public MoveScripts MoveScriptsOn(StackExchange.Redis.IDatabase database) => new(database, Scripts, Version);
}
