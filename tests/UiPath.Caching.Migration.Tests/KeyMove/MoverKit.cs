using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A group in S3 with one managed cache over the real scripts, its key space, the mover's targets and host, and the readings the tests make.
internal sealed class MoverKit : IAsyncDisposable
{
    public const long MoveId = 7;

    private readonly string _lPrefix;
    private readonly string _nHead;
    private readonly string _nTail;

    private MoverKit(KeyMoveKit kit, bool twoOwners, bool hashCache, bool noOrigin, TimeSpan? groupMin, TimeSpan? cacheMin, string? excluded, ISupersessionHandler? supersession, Func<MoverKit, IMoveSide<Item>>? source, ISchedulePoint? schedule)
    {
        Kit = kit;
        _lPrefix = kit.L(string.Empty);

        // A native key is a head, the caller key and a tail, which a cluster's tag needs.
        var native = kit.N("\u0001").Split('\u0001');
        (_nHead, _nTail) = (native[0], native[1]);
        var caches = new List<KeyMoveCache> { Cache(kit.Cid, noOrigin, cacheMin, twoOwners ? k => k.StartsWith('a') || k.StartsWith('s') : null) };
        if (twoOwners)
        {
            caches.Add(Cache(kit.Cid + "b", noOrigin, cacheMin, k => k.StartsWith('b') || k.StartsWith('s')));
        }

        if (hashCache)
        {
            caches.Add(Cache(kit.Cid + "h", false, null, _ => false, hash: true));
        }

        Space = new RegisteredKeySpace(caches, _nHead, null, excluded is null ? null : [excluded], groupMin is { } g ? new Dictionary<string, TimeSpan> { [kit.Group] = g } : null);
        var parts = new KeyMoveClientParts<Item>(kit.Group, kit.Cid, noOrigin, kit.L, kit.N, ItemCodec.Instance, ItemCodec.Instance, kit.Plain, kit.Plane, kit.Clock, () => TimeSpan.FromSeconds(30))
        {
            Options = new KeyMoveOptions(),
            Source = source?.Invoke(this),
            Schedule = schedule ?? NoSchedulePoint.Instance,
        };
        Target = new KeyMover<Item>(parts, supersession);
        Units = new MultiplexerScanUnits(() => kit.Mux);
    }

    public static MoverOptions Quick { get; } = new() { PollInterval = TimeSpan.FromMilliseconds(40), PassPause = TimeSpan.FromHours(1), MinScanInterval = TimeSpan.Zero };

    public KeyMoveKit Kit { get; }

    public RegisteredKeySpace Space { get; }

    public KeyMover<Item> Target { get; }

    public IScanUnits Units { get; private set; }

    public IDatabase Db => Kit.Db;

    public static async Task<MoverKit> CreateAsync(
        RedisFixture fixture,
        bool twoOwners = false,
        bool hashCache = false,
        bool noOrigin = false,
        TimeSpan? groupMin = null,
        TimeSpan? cacheMin = null,
        string? excluded = null,
        ISupersessionHandler? supersession = null,
        Func<MoverKit, IMoveSide<Item>>? source = null,
        Func<IDatabase, IDatabase>? interceptDb = null,
        bool cluster = false,
        int? database = null,
        ISchedulePoint? schedule = null,
        TimeProvider? clock = null)
    {
        var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: noOrigin, cluster: cluster, interceptDb: interceptDb, database: database, clock: clock);
        try
        {
            var made = new MoverKit(kit, twoOwners, hashCache, noOrigin, groupMin, cacheMin, excluded?.Replace("{U}", kit.Unique, StringComparison.Ordinal), supersession, source, schedule);
            return made;
        }
        catch
        {
            await kit.DisposeAsync();
            throw;
        }
    }

    public static async Task WaitAsync(Func<Task<bool>> done, int seconds = 30)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!await done())
        {
            if (TimeProvider.System.GetElapsedTime(started) > TimeSpan.FromSeconds(seconds))
            {
                throw new TimeoutException("The condition did not hold in time.");
            }

            await Task.Delay(25);
        }
    }

    /// <summary>Values by key; the keys get control records, then the group goes to S3.</summary>
    public async Task SeedAsync(IReadOnlyDictionary<string, (string Value, TimeSpan? Ttl)> keys)
    {
        await Kit.PrepareAsync([.. keys.Keys]);
        await Kit.SetStateAsync("S3");
        foreach (var (key, (value, ttl)) in keys)
        {
            if (ttl is { } life)
            {
                await Db.StringSetAsync(Kit.L(key), value, life);
            }
            else
            {
                await Db.StringSetAsync(Kit.L(key), value);
            }
        }
    }

    public Task SeedAsync(params string[] keys) => SeedAsync(keys.ToDictionary(k => k, k => ("v-" + k, (TimeSpan?)null)));

    public MoverHost Host(MoverOptions? options = null, Func<IMoveTarget, IMoveTarget>? wrap = null, Func<StateRecord?>? state = null, IKeyMovePlane? plane = null, IScanUnits? units = null, Func<CancellationToken, Task<StateRecord?>>? readState = null)
    {
        IMoveTarget target = Target;
        return new MoverHost(
            Kit.Group,
            plane ?? Kit.Plane,
            Space,
            [wrap?.Invoke(target) ?? target],
            units ?? Units,
            readState ?? (_ => Task.FromResult(state is null ? State() : state())),
            string.Empty,
            Kit.Clock,
            options ?? Quick,
            new FixedMoveRate(0, Kit.Clock));
    }

    public StateRecord State(string state = "S3", bool acked = true) => new(
        state,
        "fwd",
        Kit.Ep,
        Kit.Id,
        true,
        1,
        string.Empty,
        MoveId,
        Kit.Cid,
        string.Empty,
        acked ? Kit.Id : string.Empty,
        string.Empty,
        string.Empty,
        string.Empty);

    /// <summary>A plane over the same data whose database can be frozen, so a host on it dies as a killed process does: its commands fail and its lease stays held.</summary>
    public (StubPlane Plane, FreezableDatabase Freezer) FreezablePlane()
    {
        var data = Kit.Plane.Data!;
        var db = FreezableDatabase.Over(data.Db, out var freezer);
        var plane = new StubPlane(Kit.Group, Kit.Gate) { Data = data with { Db = db, Scripts = new MoveScripts(db, true, new Version(7, 0)) }, Rules = Kit.Plane.Rules };
        return (plane, freezer);
    }

    public async Task<string[]> UnitIdsAsync() => [.. (await Units.ListAsync(CancellationToken.None)).Select(u => u.Id)];

    /// <summary>Starts the host, waits until every unit has a finished-pass record, and stops it.</summary>
    public async Task RunOnePassAsync(MoverHost host)
    {
        host.Start();
        try
        {
            await WaitAsync(async () => await PassedAsync());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    public async Task<bool> PassedAsync()
    {
        foreach (var id in await UnitIdsAsync())
        {
            if (!await Db.KeyExistsAsync(Kit.Names.UnitPass(Kit.Group, id, "fwd", MoveId)))
            {
                return false;
            }
        }

        return true;
    }

    public async Task<Dictionary<string, long>> ProgressAsync() =>
        (await Db.HashGetAllAsync(Kit.Names.Progress(Kit.Group))).ToDictionary(e => e.Name.ToString(), e => (long)e.Value);

    /// <summary>Waits, bounded, until the progress hash holds exactly <paramref name="expected"/>; the counters are written after the keys are moved, so a Redis effect is no sign that they are there.</summary>
    public async Task WaitProgressAsync(IReadOnlyDictionary<string, long> expected)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            var now = await ProgressAsync();
            if (now.Count == expected.Count && expected.All(e => now.GetValueOrDefault(e.Key, -1) == e.Value))
            {
                return;
            }

            if (TimeProvider.System.GetElapsedTime(started) > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("The counters did not reach the expected values: " + string.Join(", ", now.Select(e => $"{e.Key}={e.Value}")));
            }

            await Task.Delay(25);
        }
    }

    public ValueTask DisposeAsync() => Kit.DisposeAsync();

    private KeyMoveCache Cache(string name, bool noOrigin, TimeSpan? min, Func<string, bool>? owns, bool hash = false)
    {
        static string? Parse(string key, string prefix) => key.StartsWith(prefix, StringComparison.Ordinal) ? Rest(key[prefix.Length..]) : null;

        // A key that starts with "weird:" reads as the caller key "weird", which does not map back to it.
        string? ParseNative(string key) => key.StartsWith(_nHead, StringComparison.Ordinal) && key.EndsWith(_nTail, StringComparison.Ordinal) && key.Length >= _nHead.Length + _nTail.Length ? Rest(key[_nHead.Length..^_nTail.Length]) : null;

        static string Rest(string rest) => rest.StartsWith("weird:", StringComparison.Ordinal) ? "weird" : rest;
        return new KeyMoveCache(
            name,
            Kit.Group,
            noOrigin,
            false,
            false,
            null,
            _lPrefix + "*",
            Kit.L,
            k => Parse(k, _lPrefix),
            _nHead + "*" + _nTail,
            Kit.N,
            ParseNative,
            owns is null ? null : k => k.StartsWith(_lPrefix, StringComparison.Ordinal) && owns(k[_lPrefix.Length..]),
            owns is null ? null : k => ParseNative(k) is { } caller && owns(caller),
            min,
            hash);
    }
}
