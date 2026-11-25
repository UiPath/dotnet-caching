using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// Servers that fail while keys move: a failover or a restart in the middle of a mover pass loses and repeats no key, and a write that a failover
// drops on the shard of N never brings an older value back.
[Collection("RedisIntegration")]
[Trait("Category", "Chaos")]
public sealed class KeyMoveFaultTests(RedisFixture fixture)
{
    private static readonly string[] Keys = [.. Enumerable.Range(0, 30).Select(i => "k" + i)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("N")]
    [InlineData("L")]
    public async Task A_failover_in_the_middle_of_a_mover_pass_moves_every_key_once(string side)
    {
        await using var cluster = await ClusterKit.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var pass = new MoverPassCompletion();
        await using var kit = await MoverKit.CreateAsync(fixture, cluster: true, clock: clock, interceptDb: pass.Observe);
        await SeedMoverAsync(kit);
        clock.Advance(TimeSpan.FromSeconds(1));
        var faultKey = side == "N" ? kit.Kit.N(Keys[9]) : kit.Kit.L(Keys[9]);
        var before = cluster.MasterOf(faultKey).EndPoint;
        var failedOver = false;
        var counting = new CountingTarget(kit.Target)
        {
            Before = async (key, _) =>
            {
                if (key == Keys[9] && !failedOver)
                {
                    await cluster.FailoverAsync(faultKey);
                    failedOver = true;
                }

                return null;
            },
        };

        await pass.RunAsync(kit, kit.Host(wrap: _ => counting), Ct);

        failedOver.Should().BeTrue();
        cluster.MasterOf(faultKey).EndPoint.Should().NotBe(before);
        await ExpectAllMovedAsync(kit, counting);
    }

    [Fact]
    public async Task A_replica_restarted_in_the_middle_of_a_mover_pass_leaves_the_pass_whole()
    {
        await using var cluster = await ClusterKit.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var pass = new MoverPassCompletion();
        await using var kit = await MoverKit.CreateAsync(fixture, cluster: true, clock: clock, interceptDb: pass.Observe);
        await SeedMoverAsync(kit);
        clock.Advance(TimeSpan.FromSeconds(1));
        var master = cluster.MasterOf(kit.Kit.L(Keys[5]));
        var unit = (await kit.Units.ListAsync(Ct)).Single(u => Equals(u.Primary.EndPoint, master.EndPoint));
        var replica = cluster.ReplicaOf(master);
        unit.ScanServer.EndPoint.Should().Be(replica.EndPoint);
        var runId = await ClusterKit.ReadRunIdAsync(replica);
        var restarted = false;
        var counting = new CountingTarget(kit.Target)
        {
            Before = async (key, _) =>
            {
                if (key == Keys[5] && !restarted)
                {
                    await cluster.RestartAsync(replica);
                    restarted = true;
                }

                return null;
            },
        };

        await pass.RunAsync(kit, kit.Host(wrap: _ => counting), Ct);

        restarted.Should().BeTrue();
        (await ClusterKit.ReadRunIdAsync(replica)).Should().NotBe(runId);
        await ExpectAllMovedAsync(kit, counting);
    }

    // The fence bump of a write is lost on the shard of N while its delete of L survives: the key reads as a miss, never as the older value of L.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_write_whose_N_step_a_failover_drops_leaves_a_miss_and_never_the_older_value(bool scripts)
    {
        await using var cluster = await ClusterKit.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, cluster: true, clock: clock);
        var key = await PrepareLostWriteAsync(cluster, kit);
        var master = cluster.MasterOf(kit.N(key));
        clock.Advance(TimeSpan.FromSeconds(1));

        await cluster.LoseWritesAsync(kit.N(key), async () =>
        {
            var written = await kit.Client.SetAsync(key, new Item("v1"), null, Ct);
            written.Outcome.Should().Be(WriteOutcome.Committed);
            ((string?)await master.ExecuteAsync("GET", kit.N(key))).Should().Be("v1");
            ((string?)await master.ExecuteAsync("GET", kit.Names.ForwardFence(kit.N(key)))).Should().NotBeNullOrEmpty();
        });
        await kit.Mux.ConfigureAsync();

        (await kit.ExistsAsync(kit.L(key))).Should().BeFalse("the delete of L survived on its own shard");
        var read = await kit.Client.ReadAsync(key, Ct);
        read.Kind.Should().Be(ReadOutcome.Absent, $"the older value never comes back, and the read found {read.Value?.Name}");
    }

    // A promotion that publishes N and deletes L, where the failover drops the publish: a miss, which the origin refills, never an older value.
    [Fact]
    public async Task A_promotion_whose_publish_a_failover_drops_leaves_a_miss()
    {
        await using var cluster = await ClusterKit.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var kit = await KeyMoveKit.CreateAsync(fixture, cluster: true, clock: clock);
        var key = await PrepareLostWriteAsync(cluster, kit);
        var master = cluster.MasterOf(kit.N(key));
        clock.Advance(TimeSpan.FromSeconds(1));

        await cluster.LoseWritesAsync(kit.N(key), async () =>
        {
            (await kit.Client.ReadAsync(key, Ct)).Value!.Name.Should().Be("v0");
            ((string?)await master.ExecuteAsync("GET", kit.N(key))).Should().Be("v0");
        });
        await kit.Mux.ConfigureAsync();

        (await kit.ExistsAsync(kit.N(key))).Should().BeFalse("the publish was lost");
        (await kit.ExistsAsync(kit.L(key))).Should().BeFalse("the compare-delete of L survived");
        var read = await kit.Client.ReadAsync(key, Ct);
        read.Kind.Should().Be(ReadOutcome.Absent, $"the read found {read.Value?.Name}");
    }

    private static async Task SeedMoverAsync(MoverKit kit)
    {
        await kit.SeedAsync(Keys);
        foreach (var unit in await kit.Units.ListAsync(Ct))
        {
            await WaitForReplicaAsync(unit.Primary);
        }
    }

    private static async Task<string> PrepareLostWriteAsync(ClusterKit cluster, KeyMoveKit kit)
    {
        var key = ApartKey(cluster, kit);
        await kit.PrepareAsync(key);
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L(key), "v0");
        await WaitForReplicaAsync(kit.Mux.GetServer(cluster.MasterOf(kit.L(key)).EndPoint));
        await WaitForReplicaAsync(kit.Mux.GetServer(cluster.MasterOf(kit.N(key)).EndPoint));
        return key;
    }

    private static async Task WaitForReplicaAsync(IServer writer) =>
        ((long)await writer.ExecuteAsync("WAIT", "1", "1000")).Should().BeGreaterThanOrEqualTo(1);

    // A key whose L and N sit on different masters, so a fault of one shard leaves the other.
    private static string ApartKey(ClusterKit cluster, KeyMoveKit kit)
    {
        var key = Enumerable.Range(0, 200).Select(i => "a" + i).First(k => !Equals(cluster.MasterOf(kit.L(k)).EndPoint, cluster.MasterOf(kit.N(k)).EndPoint));
        return key;
    }

    private static async Task ExpectAllMovedAsync(MoverKit kit, CountingTarget counting)
    {
        foreach (var key in Keys)
        {
            (await kit.Kit.GetAsync(kit.Kit.N(key))).Should().Be("v-" + key, $"{key} was moved");
            (await kit.Kit.ExistsAsync(kit.Kit.L(key))).Should().BeFalse(key);
            counting.Moved(key).Should().Be(1, key);
        }
    }
}
