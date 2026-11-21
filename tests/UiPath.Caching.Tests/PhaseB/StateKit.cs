using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

// One test's connection, names, state store, transition sequence and group; the group's keys are deleted on dispose.
internal sealed class StateKit : IAsyncDisposable
{
    public const string Library = "lib:";

    private StateKit(ConnectionMultiplexer mux, MoveKeyNames names, PhaseBStateOptions options, bool useScripts, Version version, string prefix)
    {
        Mux = mux;
        Names = names;
        Options = options;
        Group = "g" + Guid.NewGuid().ToString("N")[..12];
        var database = mux.GetDatabase();
        ControlDb = prefix.Length == 0 ? database : StackExchange.Redis.KeyspaceIsolation.DatabaseExtensions.WithKeyPrefix(database, prefix);
        Scripts = new MoveScripts(database, useScripts, version);
        Real = new MultiplexerTopology(mux, prefix, useScripts, version, options.ExpectedReplicasPerMaster);
        Topology = new HookedTopology(Real);
        Sampler = new ControlSampler(ControlDb, names, Topology);
        Store = new StateStore(database, names, Scripts, Sampler, TimeProvider.System);
        Sequence = new TransitionSequence(Topology, names, options, (kind, detail) => Reports.Add((kind, detail)));
    }

    public ConnectionMultiplexer Mux { get; }

    public MoveKeyNames Names { get; }

    public PhaseBStateOptions Options { get; }

    public string Group { get; }

    public IDatabase Db => Mux.GetDatabase();

    public IDatabase ControlDb { get; }

    public MoveScripts Scripts { get; }

    public MultiplexerTopology Real { get; }

    public HookedTopology Topology { get; }

    public ControlSampler Sampler { get; }

    public StateStore Store { get; }

    public TransitionSequence Sequence { get; }

    public List<(string Kind, string Detail)> Reports { get; } = [];

    public RedisKey StateKey => Names.State(Group);

    public static async Task<StateKit> StandaloneAsync(RedisContainerFixture fixture, bool useScripts, PhaseBStateOptions? options = null, string prefix = "")
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        return new StateKit(mux, new MoveKeyNames(prefix, Library, cluster: false), options ?? new PhaseBStateOptions(), useScripts, fixture.ServerVersion, prefix);
    }

    public static async Task<StateKit> ClusterAsync(bool useScripts, PhaseBStateOptions? options = null, string prefix = "")
    {
        var connectionString = Environment.GetEnvironmentVariable("REDIS_CLUSTER_CONNECTION_STRING");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(connectionString), "Set REDIS_CLUSTER_CONNECTION_STRING to a Redis Cluster to run.");
        var mux = await ConnectionMultiplexer.ConnectAsync(connectionString!);
        var version = mux.GetServers().Min(s => s.Version)!;
        return new StateKit(mux, new MoveKeyNames(prefix, Library, cluster: true), options ?? new PhaseBStateOptions(), useScripts, version, prefix);
    }

    // The target of a transition from the stored state: state, direction, nPop and one more epoch under a fresh id.
    public static StateTarget TargetFrom(StateRecord from, string state, string direction, int nPop) =>
        new(state, direction, nPop, from.Ep + 1, MoveScripts.NewNonce(), StateRecord.FormatPrev(from.State, from.Direction, from.NPop, from.Ep, from.Id), 1);

    public static StateExpectation Expecting(StateRecord from) => StateExpectation.Holding(from.Ep, from.Id, 1);

    // Writes the S0 state with a durable identity, as Init does, and returns it.
    public async Task<StateRecord> InitAsync(params string[] caches)
    {
        (await Store.InitAsync(Group, caches, CancellationToken.None)).Code.Should().Be(MoveScripts.StateCreated);
        return (await Store.ReadAsync(Group, CancellationToken.None))!;
    }

    public async Task<HashEntry[]> ControlAsync(int slot) => await ControlDb.HashGetAllAsync(Names.Control(Group, slot));

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Db.KeyDeleteAsync(StateKey);
            foreach (var chunk in Names.ControlRecords(Group).Chunk(512))
            {
                await Task.WhenAll(chunk.Select(r => ControlDb.KeyDeleteAsync(r.Key)));
            }
        }
        finally
        {
            await Mux.DisposeAsync();
        }
    }
}
