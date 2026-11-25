using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// A group in S1 over a real connection with managed caches a and b, a key space of unique prefixes, and the purge and inventory over them.
internal sealed class DataOpsKit : IAsyncDisposable
{
    private readonly List<string> _made = [];
    private readonly bool _started;

    private DataOpsKit(StateKit kit, string prefix, int batch, bool started)
    {
        Kit = kit;
        _started = started;
        Prefix = prefix;
        var tag = "km-data" + Guid.NewGuid().ToString("N")[..10];
        Space = new FakeKeySpace(tag + ":n:", tag + ":l:");
        Scanner = new MasterScanner(() => kit.Mux.GetServers(), kit.Topology, 0, prefix, kit.Time, TimeSpan.Zero);
        DataScripts = new MoveScripts(kit.ControlDb, true, kit.Mux.GetServers().Min(s => s.Version)!);
        Data = new GroupDataOps(Scanner, Space, kit.Store, kit.Scripts, DataScripts, kit.Names, batch);
        Inventory = new KeyInventory(Scanner, Space, kit.Store, kit.Db, prefix, kit.Time);
    }

    public StateKit Kit { get; }

    public string Prefix { get; }

    public IDatabase ControlDb => Kit.ControlDb;

    public FakeKeySpace Space { get; }

    public MasterScanner Scanner { get; }

    public MoveScripts DataScripts { get; }

    public GroupDataOps Data { get; }

    public KeyInventory Inventory { get; }

    public string Group => Kit.Group;

    public static async Task<DataOpsKit> StandaloneAsync(RedisContainerFixture fixture, bool useScripts, string prefix = "", int batch = 128, bool started = true)
    {
        var kit = await StateKit.StandaloneAsync(fixture, useScripts, prefix: prefix);
        var data = new DataOpsKit(kit, prefix, batch, started);
        await data.StartAsync();
        return data;
    }

    public static async Task<DataOpsKit> ClusterAsync(string prefix, int batch = 128)
    {
        var kit = await StateKit.ClusterAsync(useScripts: true, prefix: prefix);
        var data = new DataOpsKit(kit, prefix, batch, true);
        await data.StartAsync();
        return data;
    }

    public async Task<StateRecord> StateAsync() => (await Kit.Store.ReadAsync(Group, CancellationToken.None))!;

    // The logical key of a cache on a side, written under the connector prefix; a ttl of null leaves it without expiry.
    public async Task<string> SetAsync(MoveSide side, string cache, string name, TimeSpan? ttl = null, string value = "v")
    {
        var key = $"{Space.PrefixOf(side)}{cache}:{name}";
        _made.Add(Prefix + key);
        await Kit.Db.StringSetAsync(Prefix + key, value);
        if (ttl is { } life)
        {
            await Kit.Db.KeyExpireAsync(Prefix + key, life);
        }

        return key;
    }

    public async Task<bool> ExistsAsync(string key) => await Kit.Db.KeyExistsAsync(Prefix + key);

    // The group moves on under a new identity through the real sequence, which rewrites every control record.
    public async Task<StateRecord> MoveToAsync(string state, string direction = "fwd", int nPop = 0)
    {
        var from = await StateAsync();
        var result = await Kit.Sequence.RunAsync(Group, StateKit.Expecting(from), StateKit.TargetFrom(from, state, direction, nPop) with { MoveId = from.State == "S0" && state == "S1" ? from.Ep + 1 : null }, null, CancellationToken.None);
        result.Outcome.Should().Be(TransitionOutcome.Completed);
        return await StateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_made.Count > 0)
            {
                await Task.WhenAll(_made.Select(k => Kit.Db.KeyDeleteAsync(k)));
            }
        }
        finally
        {
            await Kit.DisposeAsync();
        }
    }

    private async Task StartAsync()
    {
        await Kit.InitAsync("a", "b");
        if (_started)
        {
            await MoveToAsync("S1");
        }
    }
}
