using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <param name="ConfiguredReplicas">Every replica the topology lists for the master, failed ones included, or the configured expectation; never only the connected ones.</param>
internal sealed record MasterLayout(EndPoint Endpoint, IReadOnlyList<SlotRange> Slots, int ConfiguredReplicas)
{
    /// <summary>A standalone connection that was not told how many replicas it has: a barrier on it is refused, never skipped.</summary>
    public const int UnknownReplicas = -1;
}
