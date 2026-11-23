using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

internal sealed class FakeStateReader : IStateReader
{
    private int _reads;
    private int _probes;
    private int _samples;

    public List<string> Calls { get; } = [];

    public StateRecord? State { get; set; }

    public ControlSample? Sample { get; set; }

    public bool Fail { get; set; }

    public bool FailProbe { get; set; }

    public int Reads => Volatile.Read(ref _reads);

    public int Samples => Volatile.Read(ref _samples);

    public int Probes => Volatile.Read(ref _probes);

    public static StateRecord Record(string state, long ep, string id = "id", bool durable = true, string direction = "fwd", int nPop = 0, string swept = "") =>
        new(state, direction, ep, id, durable, nPop, string.Empty, null, "a", "cs", string.Empty, swept, string.Empty, string.Empty);

    public Task<StateRecord?> ReadAsync(string group, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        Record("read");
        return Fail ? Task.FromException<StateRecord?>(new TimeoutException("state shard down")) : Task.FromResult(State);
    }

    public Task<ControlSample?> SampleAsync(string group, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _samples);
        Record("sample");
        return Answer();
    }

    public Task<ControlSample?> ProbeAsync(string group, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _probes);
        Record("probe");
        return Answer();
    }

    // A monitor runs one loop per group, so calls arrive from several threads at once.
    private void Record(string call)
    {
        lock (Calls)
        {
            Calls.Add(call);
        }
    }

    private Task<ControlSample?> Answer() =>
        FailProbe || (Fail && Sample is null) ? Task.FromException<ControlSample?>(new TimeoutException("probe failed")) : Task.FromResult(Sample);
}
