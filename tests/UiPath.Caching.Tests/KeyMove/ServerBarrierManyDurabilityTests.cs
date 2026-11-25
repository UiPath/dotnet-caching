using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The batch form of the server barrier against a real master and replica: set MOVE_SIDES_MASTER and MOVE_SIDES_REPLICA (the replica of that master).</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class ServerBarrierManyDurabilityTests
{
    private static string? Master => Environment.GetEnvironmentVariable("MOVE_SIDES_MASTER");

    private static string? Replica => Environment.GetEnvironmentVariable("MOVE_SIDES_REPLICA");

    [Fact]
    public async Task Transactions_on_one_connection_are_confirmed_by_one_wait_and_all_are_on_the_replica()
    {
        Assert.SkipUnless(Master is not null && Replica is not null, "Set MOVE_SIDES_MASTER and MOVE_SIDES_REPLICA to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{Master},allowAdmin=true,syncTimeout=10000");
        await using var replica = await ConnectionMultiplexer.ConnectAsync($"{Replica},allowAdmin=true");
        var keys = Enumerable.Range(0, 5).Select(i => (RedisKey)$"sidetest:{{{Guid.NewGuid():N}}}:k{i}").ToArray();
        try
        {
            var barrier = new ServerBarrier(mux.GetDatabase(), keys[0], new ManualTimeProvider(DateTimeOffset.UnixEpoch), replicaFree: false);

            var outcome = await barrier.WriteManyAndWaitAsync([.. keys.Select<RedisKey, Action<ITransaction>>(k => t => _ = t.StringSetAsync(k, "v"))], wayBack: false);

            outcome.Should().Be(BarrierOutcome.Confirmed);
            foreach (var key in keys)
            {
                (await replica.GetDatabase().StringGetAsync(key, CommandFlags.DemandReplica)).ToString().Should().Be("v", "a confirmed batch is on the replica");
            }
        }
        finally
        {
            await mux.GetDatabase().KeyDeleteAsync(keys);
        }
    }

    [Fact]
    public async Task A_connection_killed_between_the_transactions_and_the_wait_is_caught_by_the_server_and_confirms_only_on_the_second_attempt()
    {
        Assert.SkipUnless(Master is not null && Replica is not null, "Set MOVE_SIDES_MASTER and MOVE_SIDES_REPLICA to run.");
        await using var barrierMux = await ConnectionMultiplexer.ConnectAsync($"{Master},allowAdmin=true,syncTimeout=10000");
        await using var admin = await ConnectionMultiplexer.ConnectAsync($"{Master},allowAdmin=true");
        await using var replica = await ConnectionMultiplexer.ConnectAsync($"{Replica},allowAdmin=true");
        var keys = Enumerable.Range(0, 3).Select(i => (RedisKey)$"sidetest:{{{Guid.NewGuid():N}}}:k{i}").ToArray();
        var real = barrierMux.GetServer(barrierMux.GetEndPoints()[0]);
        var waits = 0;
        var writes = 0;

        // The first WAIT runs only after its connection was killed and replaced: on the new connection it would count a vacuous offset.
        var server = InterceptingProxy.Wrap(real, (method, args) =>
            method.Name == nameof(IServer.ExecuteAsync) && args[0] is "WAIT" && Interlocked.Increment(ref waits) == 1 ? KillThenWaitAsync(barrierMux, real, admin, (object[])args[1]!) : InterceptingProxy.Proceed);
        var mux = InterceptingProxy.Wrap<IConnectionMultiplexer>(barrierMux, (method, _) => method.Name == nameof(IConnectionMultiplexer.GetServer) ? server : InterceptingProxy.Proceed);
        var db = InterceptingProxy.Wrap(barrierMux.GetDatabase(), (method, _) => method.Name == "get_Multiplexer" ? mux : InterceptingProxy.Proceed);
        try
        {
            var barrier = new ServerBarrier(db, keys[0], new ManualTimeProvider(DateTimeOffset.UnixEpoch), replicaFree: false);

            var outcome = await barrier.WriteManyAndWaitAsync(
                [.. keys.Select<RedisKey, Action<ITransaction>>(k => t =>
                {
                    if (k == keys[0])
                    {
                        Interlocked.Increment(ref writes);
                    }

                    _ = t.StringSetAsync(k, "v");
                })],
                wayBack: false);

            outcome.Should().Be(BarrierOutcome.Confirmed);
            writes.Should().Be(2, "the first WAIT ran on another connection than the writes, so every transaction and the WAIT ran again");
            waits.Should().Be(2);
            foreach (var key in keys)
            {
                (await replica.GetDatabase().StringGetAsync(key, CommandFlags.DemandReplica)).ToString().Should().Be("v");
            }
        }
        finally
        {
            await admin.GetDatabase().KeyDeleteAsync(keys);
        }
    }

    private static async Task<RedisResult> KillThenWaitAsync(IConnectionMultiplexer connection, IServer real, ConnectionMultiplexer admin, object[] args)
    {
        await RedisSignals.ReplaceConnectionAsync(connection, admin, real);
        return await real.ExecuteAsync("WAIT", args);
    }
}
