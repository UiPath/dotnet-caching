using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

// The node hash as the leader sees it: every node acknowledges whatever durable identity the stored state shows, unless the test says it does not.
internal sealed class SimulatedFleet(StateStore store, string group) : IAdvertSource
{
    private int _reads;
    private (int After, Action Act)? _hook;

    public List<FleetNode> Nodes { get; } = [new FleetNode("n1"), new FleetNode("n2")];

    public int Reads => Volatile.Read(ref _reads);

    public void All(Action<FleetNode> change) => Nodes.ForEach(change);

    // Runs once, on the read after <paramref name="reads"/> more reads.
    public void AfterReads(int reads, Action act) => _hook = (Reads + reads, act);

    public async Task<IReadOnlyList<NodeAdvert>> ReadAsync(CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _reads);
        if (_hook is { } hook && count > hook.After)
        {
            _hook = null;
            hook.Act();
        }

        var state = await store.ReadAsync(group, cancellationToken);
        return
        [
            .. Nodes.Select(n => PlannerKit.Advert(
                n.Name,
                fl: n.Fl,
                pb: n.Pb,
                pin: n.Pin,
                pbg: n.Pbg,
                ca: n.Ca,
                acks: n.Acks && state is { Durable: true } ? state : null,
                pi: n.Pi,
                rc: n.Recycles && state is not null ? state.Ep : null,
                byp: n.Byp,
                group: group) with { Age = n.Age }),
        ];
    }
}
