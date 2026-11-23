using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

internal sealed class FakeGroupFacts : IGroupFacts
{
    public bool NoOrigin { get; set; }

    public bool Claims { get; set; }

    public bool AcceptRefill { get; set; }

    public bool Settled { get; set; } = true;

    public bool Fits { get; set; } = true;

    public bool NodeListSettled { get; set; } = true;

    public bool ReleaseFloorAttested { get; set; }

    public TimeSpan? SourceTtl { get; set; }

    public List<string> Started { get; } = [];

    public List<string> Evidence { get; } = [];

    public bool HasNoOrigin(string group, IReadOnlyList<string> cacheIds) => NoOrigin;

    public bool TakesClaim(string group) => Claims;

    public string ClaimDescriptor(string group) => "rt";

    public TimeSpan? LongestSourceTtl(string group) => SourceTtl;

    public bool AllAcceptRefill(string group, IReadOnlyList<string> cacheIds) => AcceptRefill;

    public IReadOnlyList<string> StartTaken(string group)
    {
        Started.Add(group);
        return Evidence;
    }

    public bool ClaimWired(string group) => true;

    public bool FormatSettled(string group, StateRecord state) => Settled;

    public bool NodeListComplete(string group, StateRecord state) => NodeListSettled;

    public bool MemoryFits(string group, string reportId, long estimatedBytes, double headroom) => Fits;
}
