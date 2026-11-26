using System.Net;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>A keyed batch over two masters of a real cluster, in which one master commits and the other's conditions fail.</summary>
public sealed class KeyedPartialClusterTests
{
    [Fact]
    public async Task The_barrier_reports_a_partial_application_when_one_master_committed_and_another_did_not()
    {
        await using var connection = await ConnectAsync();
        var db = connection.GetDatabase();
        var (a, b) = await KeysOnTwoMastersAsync(db);
        await Task.WhenAll(db.KeyDeleteAsync(a), db.KeyDeleteAsync(b));
        await db.StringSetAsync(b, "taken");
        var barrier = new ServerBarrier(db, a, new ManualTimeProvider(DateTimeOffset.UnixEpoch), replicaFree: true) { Retries = 1 };

        // A commits; B is conditioned on a key that does not exist, and it does, so B commits nothing on any attempt.
        var outcome = await barrier.WriteManyByKeyAndWaitAsync(
            [
                new KeyedWrite(a, t => _ = t.StringSetAsync(a, "written")),
                new KeyedWrite(b, t =>
                {
                    t.AddCondition(Condition.KeyNotExists(b));
                    _ = t.StringSetAsync(b, "written");
                }),
            ],
            wayBack: false);

        outcome.Should().Be(BarrierOutcome.NotConfirmed);
        barrier.LastError.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("keyed batch was partly applied");
        ((string?)await db.StringGetAsync(a)).Should().Be("written");
        ((string?)await db.StringGetAsync(b)).Should().Be("taken");
        await Task.WhenAll(db.KeyDeleteAsync(a), db.KeyDeleteAsync(b));
    }

    [Fact]
    public async Task The_condition_form_never_plans_again_after_a_partial_application_across_masters()
    {
        await using var connection = await ConnectAsync();
        var db = connection.GetDatabase();
        var (a, b) = await KeysOnTwoMastersAsync(db);
        await Task.WhenAll(db.KeyDeleteAsync(a), db.KeyDeleteAsync(b));
        var readsOfA = 0;
        var readsOfB = 0;

        // The first read of B races a write that moves B to ep 9: B's conditions fail, while A's write commits.
        async Task<HashEntry[]> Read(RedisKey key)
        {
            var read = await db.HashGetAllAsync(key, CommandFlags.DemandMaster);
            if (key == a)
            {
                Interlocked.Increment(ref readsOfA);
            }
            else if (Interlocked.Increment(ref readsOfB) == 1)
            {
                await db.HashSetAsync(b, [new("ep", 9), new("id", "other")]);
            }

            return read;
        }

        var master = new MasterCaps(new DnsEndPoint("m", 6379), new Version(7, 4), false, true, true, true, null, "noeviction");
        var caps = new SideCaps([master], ScanMode.PerShard, false, [], []);
        var barrier = new ServerBarrier(db, a, new ManualTimeProvider(DateTimeOffset.UnixEpoch), replicaFree: true) { Retries = 0 };
        var writer = new ServerRecordWriter(barrier, caps, Read);

        var write = await writer.RunManyAsync([MoveScripts.ControlSyncOp(a, Record(5, "id5")), MoveScripts.ControlSyncOp(b, Record(5, "id5"))], false);

        write.Confirmed.Should().BeFalse();
        readsOfA.Should().Be(2, "one read to plan and one read-back, and no second plan that would send A's committed write again");
        barrier.LastError.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("keyed batch was partly applied");
        ((string?)await db.HashGetAsync(a, "id")).Should().Be("id5");
        ((string?)await db.HashGetAsync(b, "id")).Should().Be("other");
        await Task.WhenAll(db.KeyDeleteAsync(a), db.KeyDeleteAsync(b));
    }

    private static ControlRecord Record(long ep, string id) => new(ep, id, "S1", "fwd", "0", "S0", "none", "0", "1", "prev", [("c1", "n1")]);

    private static async Task<ConnectionMultiplexer> ConnectAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("REDIS_CLUSTER_CONNECTION_STRING");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(connectionString), "Set REDIS_CLUSTER_CONNECTION_STRING to a Redis Cluster to run.");
        var options = ConfigurationOptions.Parse(connectionString);
        options.AllowAdmin = true;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    // Two record keys, unique to this process, whose slots belong to different masters.
    private static async Task<(RedisKey A, RedisKey B)> KeysOnTwoMastersAsync(IDatabase db)
    {
        var run = Guid.NewGuid().ToString("N");
        RedisKey first = $"partialtest:{{{run}-0}}:rec";
        var firstEndpoint = await db.IdentifyEndpointAsync(first);
        for (var i = 1; i < 64; i++)
        {
            RedisKey other = $"partialtest:{{{run}-{i}}}:rec";
            if (!Equals(await db.IdentifyEndpointAsync(other), firstEndpoint))
            {
                return (first, other);
            }
        }

        throw new InvalidOperationException("The cluster has one master; two are needed.");
    }
}
