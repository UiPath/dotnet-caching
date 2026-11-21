using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

// One test's connection, scripts, a control record at epoch 1 under "id1" with an open gate for "c1", and the keys it made (deleted on dispose).
internal sealed class MoveTestEnvironment : IAsyncDisposable
{
    public const string Id = "id1";
    public const string Cid = "c1";

    private readonly List<RedisKey> _made = [];

    private MoveTestEnvironment(ConnectionMultiplexer mux, IDatabase db, MoveScripts scripts, Version serverVersion)
    {
        Mux = mux;
        Db = db;
        Scripts = scripts;
        ServerVersion = serverVersion;
        Fence = Make("fence");
        Dst = Make("dst");
        Ctl = Make("ctl");
    }

    public ConnectionMultiplexer Mux { get; }

    public IDatabase Db { get; }

    public MoveScripts Scripts { get; }

    public Version ServerVersion { get; }

    public RedisKey Fence { get; }

    public RedisKey Dst { get; }

    public RedisKey Ctl { get; }

    public string Gate { get; private set; } = string.Empty;

    public FencedKeys Keys => new(Fence, Dst, Ctl);

    public bool SupportsPExpireTime => ServerVersion >= new Version(7, 0);

    public static async Task<MoveTestEnvironment> CreateAsync(RedisContainerFixture fixture, bool useScripts, bool openControl = true, bool allowAdmin = false)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var mux = await ConnectionMultiplexer.ConnectAsync(allowAdmin ? fixture.ConnectionString + ",allowAdmin=true" : fixture.ConnectionString);
        var env = new MoveTestEnvironment(mux, mux.GetDatabase(), new MoveScripts(mux.GetDatabase(), useScripts, fixture.ServerVersion), fixture.ServerVersion);
        if (openControl)
        {
            (await env.Scripts.ControlSyncAsync(env.Ctl, Record(1, Id, (Cid, MoveScripts.NewNonce())))).Should().Be(MoveScripts.Written);
            env.Gate = (await env.Db.HashGetAsync(env.Ctl, "c:" + Cid))!;
        }

        return env;
    }

    public static ControlRecord Record(long ep, string id, params (string Cid, string Nonce)[] gates) =>
        new(ep, id, "S1", "fwd", "n", "S0", "none", "n0", "0", "id0", gates);

    public RedisKey Make(string label)
    {
        var key = new RedisKey($"it-move-{label}-{Guid.NewGuid():N}");
        _made.Add(key);
        return key;
    }

    public async Task<long> ServerNowAsync()
    {
        var time = await Mux.GetServer(Mux.GetEndPoints()[0]).TimeAsync();
        return new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    }

    public async Task<long> ExpireTimeAsync(RedisKey key) => (long)await Db.ExecuteAsync("PEXPIRETIME", key);

    public async Task<Capture> CaptureAsync(string nonce, long fenceTtl = 30_000, string id = Id, string cid = Cid) =>
        await Scripts.CaptureAsync(Keys, id, cid, nonce, fenceTtl);

    public Publication PublicationFor(Capture capture, string newNonce, long px = MoveScripts.NoExpiry, long? tcap = null, bool onlyIfAbsent = false, string? gateSeen = null) =>
        new(capture.Token!, gateSeen ?? capture.Gate!, Cid, newNonce, 60_000, px, tcap ?? capture.Tcap, onlyIfAbsent);

    public async Task<RedisKey> StageHashAsync(long? px = 60_000)
    {
        var staging = Make("staging");
        await Db.HashSetAsync(staging, [new HashEntry("a", "1"), new HashEntry("b", "2")]);
        if (px is { } ms)
        {
            await Db.KeyExpireAsync(staging, TimeSpan.FromMilliseconds(ms));
        }

        return staging;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Db.KeyDeleteAsync([.. _made]);
        }
        finally
        {
            await Mux.DisposeAsync();
        }
    }
}
