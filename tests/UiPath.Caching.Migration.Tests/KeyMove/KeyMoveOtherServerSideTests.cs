using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveOtherServerSideTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_promotion_moves_a_value_from_L_on_the_other_server_to_N_and_deletes_L_there()
    {
        await using var env = await Env.OpenAsync(fixture, "S3");
        await env.Other.StringSetAsync(env.Kit.L("k"), "v");

        var read = await env.Kit.Client.ReadAsync("k", Ct);

        read.Value?.Name.Should().Be("v");
        (await env.Kit.GetAsync(env.Kit.N("k"))).Should().Be("v");
        (await env.Other.KeyExistsAsync(env.Kit.L("k"))).Should().BeFalse();
        (await env.Kit.ExistsAsync(env.Kit.L("k"))).Should().BeFalse();
        (await env.Other.KeyExistsAsync(env.Kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_write_and_a_delete_reach_L_on_the_other_server_and_leave_the_library_server_alone()
    {
        await using var env = await Env.OpenAsync(fixture, "S1", nPop: 0);

        (await env.Kit.Client.SetAsync("k", new Item("v1"), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await env.Other.StringGetAsync(env.Kit.L("k"))).ToString().Should().Be("v1");
        (await env.Kit.ExistsAsync(env.Kit.L("k"))).Should().BeFalse();
        (await env.Kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await env.Other.KeyExistsAsync(env.Kit.L("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_fill_writes_L_on_the_other_server_only_when_absent()
    {
        await using var env = await Env.OpenAsync(fixture, "S1", nPop: 0);
        var box = new FillBox<Item>();

        (await env.Kit.Client.FillAsync("k", new Item("fill"), null, box, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await env.Kit.Client.FillAsync("k", new Item("late"), null, box, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        (await env.Other.StringGetAsync(env.Kit.L("k"))).ToString().Should().Be("fill");
        box.Stored?.Name.Should().Be("fill");
        (await env.Kit.ExistsAsync(env.Kit.L("k"))).Should().BeFalse();
    }

    private sealed class Env : IAsyncDisposable
    {
        private readonly ConnectionMultiplexer _other;

        private Env(KeyMoveKit kit, ConnectionMultiplexer other)
        {
            Kit = kit;
            _other = other;
        }

        public KeyMoveKit Kit { get; }

        public IDatabase Other => _other.GetDatabase();

        public static async Task<Env> OpenAsync(RedisFixture fixture, string state, int nPop = 1)
        {
            var address = Environment.GetEnvironmentVariable("TWO_SERVER_SRC");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(address), "Set TWO_SERVER_SRC to run the other-server side tests.");
            Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 and REDIS_CONNECTION_STRING to run.");
            var other = await ConnectionMultiplexer.ConnectAsync(address + ",allowAdmin=true");
            var version = other.GetServers().Where(s => !s.IsReplica).Select(s => s.Version).Min() ?? new Version(7, 0);
            var scripts = new MoveScripts(other.GetDatabase(), true, version);
            var kit = await KeyMoveKit.CreateAsync(
                fixture,
                source: k => new InPlaceMoveSide<Item>(
                    new StubPlane(k.Group, new EpochGate()) { Data = new KeyMoveData(other.GetDatabase(), scripts, k.Names, () => 0, () => true) },
                    k.Group,
                    k.L,
                    ItemCodec.Instance,
                    null,
                    k.Clock));
            await kit.PrepareAsync("k");
            await kit.SetStateAsync(state, nPop);

            // L's control record lives with L, on the other server.
            var record = new ControlRecord(kit.Ep, kit.Id, "S3", "fwd", "1", "S0", "none", "0", "0", "id0", [(kit.Cid, MoveScripts.NewNonce())]);
            await scripts.ControlSyncAsync(kit.Names.ControlFor(kit.Group, kit.L("k")), record);
            return new Env(kit, other);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var server in _other.GetServers().Where(s => !s.IsReplica))
                {
                    foreach (var key in server.Keys(pattern: $"*{Kit.Unique}*").ToArray())
                    {
                        await _other.GetDatabase().KeyDeleteAsync(key);
                    }
                }
            }
            finally
            {
                await Kit.DisposeAsync();
                await _other.DisposeAsync();
            }
        }
    }
}
