using StackExchange.Redis;
using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

/// <summary>The server barrier against a real master and replica: set MOVE_SIDES_MASTER and MOVE_SIDES_REPLICA (the replica of that master).</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class ServerBarrierDurabilityTests
{
    private static string? Master => Environment.GetEnvironmentVariable("MOVE_SIDES_MASTER");

    private static string? Replica => Environment.GetEnvironmentVariable("MOVE_SIDES_REPLICA");

    [Fact]
    public async Task A_connection_killed_between_the_transaction_and_the_wait_is_caught_by_the_server_and_confirms_only_on_the_second_attempt()
    {
        Assert.SkipUnless(Master is not null && Replica is not null, "Set MOVE_SIDES_MASTER and MOVE_SIDES_REPLICA to run.");
        await using var barrierMux = await ConnectionMultiplexer.ConnectAsync($"{Master},allowAdmin=true,syncTimeout=10000");
        await using var admin = await ConnectionMultiplexer.ConnectAsync($"{Master},allowAdmin=true");
        await using var replica = await ConnectionMultiplexer.ConnectAsync($"{Replica},allowAdmin=true");
        var key = (RedisKey)$"sidetest:{{{Guid.NewGuid():N}}}:k";
        var real = barrierMux.GetServer(barrierMux.GetEndPoints()[0]);
        var waits = 0;
        var writes = 0;

        // The first WAIT runs only after its connection was killed and replaced: on the new connection it would count a vacuous offset.
        var server = InterceptingProxy.Wrap(real, (method, args) =>
            method.Name == nameof(IServer.ExecuteAsync) && args[0] is "WAIT" && Interlocked.Increment(ref waits) == 1 ? KillThenWaitAsync(real, admin, (object[])args[1]!) : InterceptingProxy.Proceed);
        var mux = InterceptingProxy.Wrap<IConnectionMultiplexer>(barrierMux, (method, _) => method.Name == nameof(IConnectionMultiplexer.GetServer) ? server : InterceptingProxy.Proceed);
        var db = InterceptingProxy.Wrap(barrierMux.GetDatabase(), (method, _) => method.Name == "get_Multiplexer" ? mux : InterceptingProxy.Proceed);
        try
        {
            var barrier = new ServerBarrier(db, key, TimeProvider.System, replicaFree: false);

            var outcome = await barrier.WriteAndWaitAsync(
                transaction =>
                {
                    Interlocked.Increment(ref writes);
                    return transaction.StringSetAsync(key, "v");
                },
                wayBack: false);

            outcome.Should().Be(BarrierOutcome.Confirmed);
            writes.Should().Be(2, "the first WAIT ran on another connection than the write, so the transaction and WAIT ran again");
            waits.Should().Be(2);
            (await replica.GetDatabase().StringGetAsync(key, CommandFlags.DemandReplica)).ToString().Should().Be("v", "a confirmed write is on the replica");
        }
        finally
        {
            await admin.GetDatabase().KeyDeleteAsync(key);
        }
    }

    private static async Task<RedisResult> KillThenWaitAsync(IServer real, IConnectionMultiplexer admin, object[] args)
    {
        var id = (long)await real.ExecuteAsync("CLIENT", "ID");
        await admin.GetServer(admin.GetEndPoints()[0]).ExecuteAsync("CLIENT", "KILL", "ID", id);
        await Task.Run(
            async () =>
            {
                while ((long)await real.ExecuteAsync("CLIENT", "ID") == id)
                {
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                }
            },
            TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        return await real.ExecuteAsync("WAIT", args);
    }
}
