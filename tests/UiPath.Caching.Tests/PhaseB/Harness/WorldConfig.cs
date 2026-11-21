using System.Collections.Immutable;

namespace UiPath.Caching.Tests.PhaseB.Harness;

/// <param name="MaxCapture">CaptureToSnapshotMax on the virtual clock.</param>
/// <param name="SplitCapReply">Gives the client a step between CAP's execution and the arrival of its reply.</param>
internal sealed record WorldConfig(ImmutableArray<KeySpec> Keys, ModelFaults Faults)
{
    public long FenceTtl { get; init; } = 100;

    public long MaxCapture { get; init; } = 2;

    public bool SplitCapReply { get; init; }

    public int Nodes { get; init; } = 2;

    public IEnumerable<string> Cids => Keys.Select(k => k.Cid).Distinct();
}
