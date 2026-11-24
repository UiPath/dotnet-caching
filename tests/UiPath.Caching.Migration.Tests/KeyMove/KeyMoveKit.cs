using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// One test's server connection, a managed cache of one group, its client over the real scripts, and control records under the identity the test sets.
internal sealed class KeyMoveKit : IAsyncDisposable
{
    private readonly List<string> _prepared = [];
    private readonly EpochGate _gate = new();
    private readonly string? _nativePrefix;
    private long _events;
    private long _ep;
    private long? _limit;

    private KeyMoveKit(ConnectionMultiplexer mux, bool useScripts, bool noOrigin, TimeProvider clock, KeyMoveOptions options, string unique, Version version, Func<KeyMoveKit, IMoveSide<Item>>? source, Func<IDatabase, IDatabase>? interceptDb, IKeyMoveCodec<Item>? inPlace, IKeyMoveCodec<Item>? native, string? nativePrefix)
    {
        _nativePrefix = nativePrefix;
        Mux = mux;
        Unique = unique;
        Group = "g" + unique;
        Cid = "c" + unique;
        var cluster = mux.GetServers().Any(s => s.ServerType == ServerType.Cluster);
        Names = new MoveKeyNames(string.Empty, $"lib{unique}:", cluster);
        Scripts = new MoveScripts(mux.GetDatabase(), useScripts, version);
        SideData = new KeyMoveData(mux.GetDatabase(), Scripts, Names, () => Interlocked.Read(ref _events), () => Connected);
        SidePlane = new StubPlane(Group, _gate) { Data = SideData };
        Plane = new StubPlane(Group, _gate) { Data = interceptDb is null ? SideData : Intercepted(SideData, interceptDb, useScripts, version) };
        Plain = new PlainPathStub();
        Clock = clock;
        Client = new KeyMoveClient<Item>(new KeyMoveClientParts<Item>(Group, Cid, noOrigin, L, N, inPlace ?? ItemCodec.Instance, native ?? ItemCodec.Instance, Plain, Plane, clock, () => TimeSpan.FromSeconds(30))
        {
            Options = options,
            Schedule = Schedule,
            RunRetriesInBackground = false,
            Source = source?.Invoke(this),
        });
    }

    public EpochGate Gate => _gate;

    public ConnectionMultiplexer Mux { get; }

    public string Unique { get; }

    public string Group { get; }

    public string Cid { get; }

    public MoveKeyNames Names { get; }

    public MoveScripts Scripts { get; }

    public StubPlane Plane { get; }

    public StubPlane SidePlane { get; }

    public KeyMoveData SideData { get; }

    public PlainPathStub Plain { get; }

    public TimeProvider Clock { get; }

    public KeyMoveClient<Item> Client { get; }

    public RecordingSchedule Schedule { get; } = new();

    public IDatabase Db => Mux.GetDatabase();

    public bool Connected { get; set; } = true;

    // The generations the records show: null for none, and with none the identity is the binding.
    public string? Fg { get; set; }

    public string? Rg { get; set; }

    public long Events => Interlocked.Read(ref _events);

    public string Id { get; private set; } = string.Empty;

    public long Ep => _ep;

    public static async Task<KeyMoveKit> CreateAsync(RedisFixture fixture, bool useScripts = true, bool noOrigin = false, bool cluster = false, TimeProvider? clock = null, KeyMoveOptions? options = null, Func<KeyMoveKit, IMoveSide<Item>>? source = null, Func<IDatabase, IDatabase>? interceptDb = null, int? database = null, IKeyMoveCodec<Item>? inPlace = null, IKeyMoveCodec<Item>? native = null, string? nativePrefix = null)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 and REDIS_CONNECTION_STRING to run.");
        var connection = fixture.ConnectionString;
        if (cluster)
        {
            connection = Environment.GetEnvironmentVariable("REDIS_CLUSTER_CONNECTION_STRING") ?? string.Empty;
            Assert.SkipWhen(string.IsNullOrWhiteSpace(connection), "Set REDIS_CLUSTER_CONNECTION_STRING to run the cluster tests.");
        }

        var mux = await ConnectionMultiplexer.ConnectAsync(connection + ",allowAdmin=true" + (database is { } db ? $",defaultDatabase={db}" : string.Empty));
        var version = mux.GetServers().Where(s => !s.IsReplica).Select(s => s.Version).Min() ?? new Version(7, 0);
        return new KeyMoveKit(mux, useScripts, noOrigin, clock ?? TimeProvider.System, options ?? new KeyMoveOptions(), Guid.NewGuid().ToString("N")[..10], version, source, interceptDb, inPlace, native, nativePrefix);
    }

    public string L(string key) => $"km4:{Unique}:L:{key}";

    // A cluster needs a tag in every native key, so its fence and control record sit in its slot.
    public string N(string key)
    {
        if (_nativePrefix is { } prefix)
        {
            return prefix + key;
        }

        return Names.Cluster ? $"km4:{{{Unique}.{key}}}:N" : $"km4:{Unique}:N:{key}";
    }

    public NegativeHints Hints() => Client.Hints;

    public void ConnectionEvent() => Interlocked.Increment(ref _events);

    // A new identity: the gate and the rules follow it, and every prepared key's control records hold it.
    public async Task SetStateAsync(string state, int nPop = 1, bool swept = false, string direction = "fwd")
    {
        await MoveGateAsync();
        ApplyRules(state, nPop, swept, direction);
    }

    // The identity moves in the gate and in the records, and the rules of the node do not know it yet.
    public async Task MoveGateAsync()
    {
        _ep++;
        Id = $"id{_ep}";
        _gate.Switch(Group, _ep, Id);
        foreach (var key in _prepared)
        {
            await WriteRecordsAsync(key);
        }
    }

    // The rules follow the identity in force.
    // The identity of a state a real transition wrote, records included: the gate and the rules of the node follow it.
    public void Adopt(StateRecord state, bool swept = false)
    {
        _ep = state.Ep;
        Id = state.Id;
        _gate.Switch(Group, _ep, Id);
        ApplyRules(state.State, state.NPop, swept, state.Direction);
    }

    public void ApplyRules(string state, int nPop = 1, bool swept = false, string direction = "fwd") =>
        Plane.Rules = new GroupRules(true, ReadRule.Follow, WriteRule.Follow, new ActedState(state, direction, _ep, Id, nPop, swept), null, false, true, [], new HashSet<string> { Cid });

    // The control records of a key's two sides, as a transition leaves them.
    public async Task PrepareAsync(params string[] keys)
    {
        foreach (var key in keys)
        {
            _prepared.Add(key);
            if (_ep > 0)
            {
                await WriteRecordsAsync(key);
            }
        }
    }

    // The rules of a state the records have not reached yet, or the other way round.
    public async Task AdvanceRecordsAsync(string key, long ep, string id)
    {
        foreach (var side in new[] { L(key), N(key) })
        {
            await Scripts.ControlSyncAsync(Names.ControlFor(Group, side), Record(ep, id));
        }
    }

    // The records of a key's slot hold room for this many more markers than they hold now: the limit the scripts judge a first marked write against.
    public async Task LimitMarkersAsync(string key, int room)
    {
        var control = Names.ControlFor(Group, N(key));
        var fields = await Db.HashLengthAsync(control);
        _limit = fields + room;
        (await Scripts.LimitSetAsync(control, Id, fields + room)).Should().Be(1);
    }

    public async Task<string?> GetAsync(string redisKey) => await Db.StringGetAsync(redisKey);

    public async Task<bool> ExistsAsync(string redisKey) => await Db.KeyExistsAsync(redisKey);

    public async Task<string?> FenceAsync(string key) => await Db.StringGetAsync(Names.ForwardFence(N(key)));

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var server in Mux.GetServers().Where(s => !s.IsReplica))
            {
                var keys = server.Keys(pattern: $"*{Unique}*").ToArray();
                foreach (var key in keys)
                {
                    await Mux.GetDatabase().KeyDeleteAsync(key);
                }
            }
        }
        finally
        {
            Client.Dispose();
            await Mux.DisposeAsync();
        }
    }

    // Record script accesses as well as direct database calls made by the client.
    private static KeyMoveData Intercepted(KeyMoveData data, Func<IDatabase, IDatabase> intercept, bool useScripts, Version version)
    {
        var db = intercept(data.Db);
        return data with { Db = db, Scripts = new MoveScripts(db, useScripts, version) };
    }

    private ControlRecord Record(long ep, string id) => new(ep, id, "S3", "fwd", "1", "S0", "none", "0", "0", "id0", [(Cid, MoveScripts.NewNonce())]) { Limit = _limit ?? ControlRecord.FixedFields + 1 + 1024, BindingField = Fg is null && Rg is null ? string.Empty : "fg", ForwardGeneration = Fg ?? string.Empty, RetiringGeneration = Rg ?? string.Empty };

    private async Task WriteRecordsAsync(string key) => await AdvanceRecordsAsync(key, _ep, Id);
}
