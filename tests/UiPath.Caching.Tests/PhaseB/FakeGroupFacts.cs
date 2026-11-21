using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

internal sealed class FakeGroupFacts : IGroupFacts
{
    public bool NoOrigin { get; set; }

    public bool Claims { get; set; }

    public bool AcceptRefill { get; set; }

    public bool Settled { get; set; } = true;

    public bool Fits { get; set; } = true;

    public bool NodeListSettled { get; set; } = true;

    public bool ReleaseFloorAttested { get; set; }

    public bool HasNoOrigin(string group, IReadOnlyList<string> cacheIds) => NoOrigin;

    public bool TakesClaim(string group) => Claims;

    public bool AllAcceptRefill(string group, IReadOnlyList<string> cacheIds) => AcceptRefill;

    public bool FormatSettled(string group) => Settled;

    public bool NodeListComplete(string group) => NodeListSettled;

    public bool MemoryFits(string group, long estimatedBytes, double headroom) => Fits;
}
