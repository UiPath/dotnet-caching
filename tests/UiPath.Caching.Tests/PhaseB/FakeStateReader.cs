using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

internal sealed class FakeStateReader : IStateReader
{
    private int _reads;
    private int _probes;

    public StateRecord? State { get; set; }

    public ControlSample? Sample { get; set; }

    public bool Fail { get; set; }

    public bool FailProbe { get; set; }

    public int Reads => Volatile.Read(ref _reads);

    public int Probes => Volatile.Read(ref _probes);

    public static StateRecord Record(string state, long ep, string id = "id", bool durable = true, string direction = "fwd", int nPop = 0, string swept = "") =>
        new(state, direction, ep, id, durable, nPop, string.Empty, null, "a", "cs", string.Empty, swept, string.Empty, string.Empty);

    public Task<StateRecord?> ReadAsync(string group, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        return Fail ? Task.FromException<StateRecord?>(new TimeoutException("state shard down")) : Task.FromResult(State);
    }

    public Task<ControlSample?> ProbeAsync(string group, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _probes);
        return FailProbe || (Fail && Sample is null) ? Task.FromException<ControlSample?>(new TimeoutException("probe failed")) : Task.FromResult(Sample);
    }
}
