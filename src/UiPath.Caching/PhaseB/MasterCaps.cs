using System.Net;

namespace UiPath.Caching.PhaseB;

/// <summary>What one master of a side can do, as probed (section 3.8).</summary>
/// <param name="EndPoint">The master the probe ran on.</param>
/// <param name="Scripts">One script of the control record's hash commands ran.</param>
/// <param name="Transactions">A watched transaction of the same commands executed.</param>
/// <param name="Hashes">The hash commands of the control records ran outside a script.</param>
/// <param name="Replicas">Null when the server refused ROLE.</param>
/// <param name="EvictionPolicy">Null when the server refused to tell.</param>
internal sealed record MasterCaps(
    EndPoint EndPoint,
    Version Version,
    bool Scripts,
    bool Transactions,
    bool Hashes,
    bool Wait,
    int? Replicas,
    string? EvictionPolicy)
{
    public bool FallbackAllowed => Version >= MoveScripts.FallbackMinimum;

    /// <summary>Control records are never evicted: <c>noeviction</c> or a <c>volatile-*</c> policy; unknown needs the operator's attestation (G12).</summary>
    public bool? EvictionSafe => EvictionPolicy is null ? null : EvictionPolicy == "noeviction" || EvictionPolicy.StartsWith("volatile-", StringComparison.Ordinal);

    public IEnumerable<string> Failures(string side)
    {
        if (!Hashes)
        {
            yield return $"{side}: {EndPoint} refuses the hash commands of the control records";
        }

        if (!Scripts && !(FallbackAllowed && Transactions))
        {
            yield return FallbackAllowed
                ? $"{side}: {EndPoint} refuses scripts and watched transactions, so neither scripts nor the fallback run"
                : $"{side}: {EndPoint} refuses scripts and Redis {Version} is older than {MoveScripts.FallbackMinimum}, so neither scripts nor the fallback run";
        }

        if (EvictionSafe == false)
        {
            yield return $"{side}: {EndPoint} maxmemory-policy {EvictionPolicy} can evict control records";
        }
    }
}
