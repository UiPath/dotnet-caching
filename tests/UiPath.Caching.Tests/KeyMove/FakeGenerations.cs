using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>Generations and baselines held in memory, with reads that can be made to fail or to change at a chosen read.</summary>
internal sealed class FakeGenerations : IGenerationSource, IAdmissionBaselines
{
    private readonly Dictionary<RecordSide, ServerGeneration?> _now = new()
    {
        [RecordSide.Src] = new ServerGeneration("src-run", "src-repl"),
        [RecordSide.Dst] = new ServerGeneration("dst-run", "dst-repl"),
    };

    private int _reads;

    public AdmissionBaseline? Dst { get; set; }

    public AdmissionBaseline? Src { get; set; }

    public bool BaselinesFail { get; set; }

    /// <summary>Runs before the Nth generation read (1-based, both sides counted together), to change a generation in the middle of a gate.</summary>
    public Action<int>? BeforeRead { get; set; }

    public int BaselineWrites { get; private set; }

    public void Set(RecordSide side, ServerGeneration? generation) => _now[side] = generation;

    public ServerGeneration? Current(RecordSide side) => _now[side];

    public Task<ServerGeneration?> ReadAsync(RecordSide side, CancellationToken cancellationToken)
    {
        BeforeRead?.Invoke(++_reads);
        return Task.FromResult(_now[side]);
    }

    public Task<AdmissionBaseline?> ReadDstAsync(string app, CancellationToken cancellationToken) => Read(Dst);

    public Task<AdmissionBaseline?> ReadSrcAsync(string app, CancellationToken cancellationToken) => Read(Src);

    public Task<bool> WriteDstAsync(string app, AdmissionBaseline baseline, AdmissionBaseline? replaces, CancellationToken cancellationToken)
    {
        if (Dst != replaces)
        {
            return Task.FromResult(false);
        }

        BaselineWrites++;
        Dst = baseline;
        return Task.FromResult(true);
    }

    public Task<bool> WriteSrcAsync(string app, AdmissionBaseline baseline, AdmissionBaseline? replaces, CancellationToken cancellationToken)
    {
        if (Src != replaces)
        {
            return Task.FromResult(false);
        }

        BaselineWrites++;
        Src = baseline;
        return Task.FromResult(true);
    }

    public Task ClearAsync(string app, string token, CancellationToken cancellationToken)
    {
        Dst = Dst?.Token == token ? null : Dst;
        Src = Src?.Token == token ? null : Src;
        return Task.CompletedTask;
    }

    private Task<AdmissionBaseline?> Read(AdmissionBaseline? baseline) =>
        BaselinesFail ? Task.FromException<AdmissionBaseline?>(new TimeoutException("the baseline cannot be read")) : Task.FromResult(baseline);
}
