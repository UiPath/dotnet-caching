using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A KeyMoveKit whose group state is a real one: written by the state store and the transition sequence, cleared by the real coordinator and N sweep.
internal sealed class ClearKit : IAsyncDisposable
{
    private ClearKit(KeyMoveKit kit)
    {
        Kit = kit;
        RetryTime = new ObservedClock(kit.Time.Manual);
        Options = new KeyMoveStateOptions { ExpectedReplicasPerMaster = 0 };
        var version = kit.Mux.GetServers().Where(s => !s.IsReplica).Select(s => s.Version).Min() ?? new Version(7, 0);
        var database = kit.Db;
        Scripts = new MoveScripts(database, true, version);
        Topology = new MultiplexerTopology(kit.Mux, string.Empty, true, version, 0, barrierWait: Options.BarrierWait);
        Sampler = new ControlSampler(database, kit.Names, Topology);
        Store = new StateStore(database, kit.Names, Scripts, Sampler, kit.Clock);
        Sequence = new TransitionSequence(Topology, kit.Names, Options, time: kit.Clock);
        Records = new ControlRecordReader(database, kit.Names);
        Coordinator = new ClearCoordinator(Store, Scripts, Scripts, Records, kit.Names, Options, kit.Clock);
        var library = "km4:";
        Cache = new KeyMoveCache(kit.Cid, kit.Group, false, false, false, null, $"{library}{kit.Unique}:L:*", k => kit.L(k), k => k[$"{library}{kit.Unique}:L:".Length..], $"{library}{kit.Unique}:N:*", k => kit.N(k), k => k[$"{library}{kit.Unique}:N:".Length..]);
        Space = new RegisteredKeySpace([Cache], library);
        var scanner = new MasterScanner(() => kit.Mux.GetServers(), Topology, 0, string.Empty, kit.Clock, TimeSpan.Zero);
        Sweep = new KeyMoveSweep(scanner, Space, database, kit.Names);
    }

    public KeyMoveKit Kit { get; }

    public ObservedClock RetryTime { get; }

    public KeyMoveStateOptions Options { get; }

    public MoveScripts Scripts { get; }

    public StateStore Store { get; }

    public ClearCoordinator Coordinator { get; }

    public KeyMoveSweep Sweep { get; }

    public KeyMoveCache Cache { get; }

    public RegisteredKeySpace Space { get; }

    public ControlRecordReader Records { get; }

    private TransitionSequence Sequence { get; }

    private MultiplexerTopology Topology { get; }

    private ControlSampler Sampler { get; }

    // The group reaches the state through the real transition sequence; the kit's client and plane adopt the identity it wrote.
    public static async Task<ClearKit> CreateAsync(RedisFixture fixture, string state, bool noOrigin = false, KeyMoveOptions? options = null, Func<KeyMoveKit, IMoveSide<Item>>? source = null)
    {
        var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: noOrigin, options: options, source: source);
        var clear = new ClearKit(kit);
        await clear.MoveToAsync(state);
        return clear;
    }

    // The sweep over a key space in which the given caches are registered, for a registration that makes two caches own one key.
    public KeyMoveSweep SweepWith(params KeyMoveCache[] caches) =>
        new(new MasterScanner(() => Kit.Mux.GetServers(), Topology, 0, string.Empty, Kit.Clock, TimeSpan.Zero), new RegisteredKeySpace(caches, "km4:"), Kit.Db, Kit.Names);

    public async Task<StateRecord> StateAsync() => (await Store.ReadAsync(Kit.Group, CancellationToken.None))!;

    // From S0 (no state yet) to S1 and on to the target, each as a completed transition.
    public async Task MoveToAsync(string state)
    {
        if (await Store.ReadAsync(Kit.Group, CancellationToken.None) is null)
        {
            (await Store.InitAsync(Kit.Group, [Kit.Cid], CancellationToken.None)).Code.Should().Be(MoveScripts.StateCreated);
        }

        foreach (var step in state == "S1" ? new[] { "S1" } : new[] { "S1", state })
        {
            var from = await StateAsync();
            var target = TargetFrom(from, step, step == "S1" ? 0 : 1) with { MoveId = from.State == "S0" ? from.Ep + 1 : null };
            (await Sequence.RunAsync(Kit.Group, StateExpectation.Holding(from.Ep, from.Id, 1), target, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
        }

        Kit.Adopt(await StateAsync());
    }

    public async Task<string?> GateAsync() => await Kit.Db.HashGetAsync(Kit.Names.Control(Kit.Group, 0), "c:" + Kit.Cid);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Kit.Db.KeyDeleteAsync([Kit.Names.State(Kit.Group), Kit.Names.Control(Kit.Group, 0), Kit.Names.ClearLock(Kit.Group, Kit.Cid)]);
        }
        finally
        {
            await Kit.DisposeAsync();
        }
    }

    private static StateTarget TargetFrom(StateRecord from, string state, int nPop) =>
        new(state, "fwd", nPop, from.Ep + 1, MoveScripts.NewNonce(), StateRecord.FormatPrev(from.State, from.Direction, from.NPop, from.Ep, from.Id), 1);
}
