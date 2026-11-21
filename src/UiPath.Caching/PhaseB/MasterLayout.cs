using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <param name="ConfiguredReplicas">Every replica the topology lists for the master, failed ones included, or the configured expectation; never only the connected ones.</param>
internal sealed record MasterLayout(EndPoint Endpoint, IReadOnlyList<SlotRange> Slots, int ConfiguredReplicas);
