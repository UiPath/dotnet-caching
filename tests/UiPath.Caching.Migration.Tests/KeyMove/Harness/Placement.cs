using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove.Harness;

// Where the harness puts L and N: one server, a cluster, or L on a server of its own, as two real servers in production would hold them.
internal sealed class Placement : IAsyncDisposable
{
    public const string One = "one";

    public const string Cluster = "cluster";

    public const string Two = "two";

    private readonly ConnectionMultiplexer? _other;
    private readonly MoveScripts? _otherScripts;

    private Placement(KeyMoveKit kit, ConnectionMultiplexer? other, MoveScripts? otherScripts)
    {
        Kit = kit;
        _other = other;
        _otherScripts = otherScripts;
    }

    public KeyMoveKit Kit { get; }

    /// <summary>The database that holds L.</summary>
    public IDatabase LDb => _other?.GetDatabase() ?? Kit.Db;

    public static async Task<Placement> OpenAsync(RedisFixture fixture, string placement, bool scripts, bool noOrigin)
    {
        if (placement != Two)
        {
            return new Placement(await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin, placement == Cluster), null, null);
        }

        var address = Environment.GetEnvironmentVariable("TWO_SERVER_SRC");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(address), "Set TWO_SERVER_SRC to run the harness with L on a server of its own.");
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 and REDIS_CONNECTION_STRING to run.");
        var other = await ConnectionMultiplexer.ConnectAsync(address + ",allowAdmin=true");
        string? unique = null;
        try
        {
            var database = other.GetDatabase();
            var version = other.GetServers().Where(s => !s.IsReplica).Select(s => s.Version).Min() ?? new Version(7, 0);
            var otherScripts = new MoveScripts(database, scripts, version);
            var kit = await KeyMoveKit.CreateAsync(
                fixture,
                scripts,
                noOrigin,
                source: k =>
                {
                    unique = k.Unique;
                    var plane = new StubPlane(k.Group, new EpochGate())
                    {
                        Data = new KeyMoveData(database, otherScripts, k.Names, () => 0, () => other.IsConnected),
                    };
                    return new InPlaceMoveSide<Item>(plane, k.Group, k.L, ItemCodec.Instance, null, k.Clock);
                });
            return new Placement(kit, other, otherScripts);
        }
        catch
        {
            await KeyMoveKit.CleanUpConnectionAsync(other, unique);
            throw;
        }
    }

    /// <summary>Writes the control record of L's slot on L's own server, where the kit does not reach, under the identity in force.</summary>
    public async Task SyncAsync(string key)
    {
        if (_otherScripts is { } scripts)
        {
            await scripts.ControlSyncAsync(Kit.Names.ControlFor(Kit.Group, Kit.L(key)), Kit.CurrentRecord());
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_other is { } other)
            {
                await KeyMoveKit.CleanUpConnectionAsync(other, Kit.Unique);
            }
        }
        finally
        {
            await Kit.DisposeAsync();
        }
    }
}
