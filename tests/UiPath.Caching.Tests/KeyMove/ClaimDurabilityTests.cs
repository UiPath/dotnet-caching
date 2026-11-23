using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>WAIT against a real replica that stalls: set CLAIM_REPLICA_CONNECTION_STRING to the replica (with the debug command enabled) of the test master.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class ClaimDurabilityTests
{
    private const string T1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string? Master => Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING");

    private static string? Replica => Environment.GetEnvironmentVariable("CLAIM_REPLICA_CONNECTION_STRING");

    [Fact]
    public async Task A_replica_that_cannot_acknowledge_leaves_the_step_unconfirmed_after_the_retries()
    {
        Assert.SkipUnless(Master is not null && Replica is not null, "Set REDIS_CONNECTION_STRING and CLAIM_REPLICA_CONNECTION_STRING to run.");
        await using var master = await ConnectionMultiplexer.ConnectAsync($"{Master},allowAdmin=true");
        await using var replica = await ConnectionMultiplexer.ConnectAsync($"{Replica},allowAdmin=true,syncTimeout=10000");
        var db = master.GetDatabase();
        var key = (RedisKey)$"claimtest:mx:{{{Guid.NewGuid():N}}}";
        try
        {
            var store = new ClaimStore(db, key, useScripts: true, new Version(7, 4)) { BarrierTimeout = TimeSpan.FromMilliseconds(200), BarrierRetries = 2 };
            (await store.OpenAsync("rt:a", T1, "rt", restore: true)).Durability.Should().Be(ClaimDurability.Confirmed);

            // The replica sleeps, so no acknowledgement arrives while the step is retried.
            var sleeping = replica.GetServer(replica.GetEndPoints()[0]).ExecuteAsync("DEBUG", "SLEEP", "2");
            await Task.Delay(100, TestContext.Current.CancellationToken);

            var result = await store.IdleAsync("rt:a", T1);

            result.Should().Be(new ClaimResult(0, ClaimDurability.NotApplicable), "idle needs an S0 cert, so nothing is written");
            var cert = await store.CertifyAsync("rt:a", T1, 5, swept: false);
            cert.Should().Be(new ClaimResult(1, ClaimDurability.NotConfirmed));
            await sleeping;
        }
        finally
        {
            await db.KeyDeleteAsync(key);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_repeat_on_a_fresh_connection_writes_again_so_its_wait_is_not_vacuous(bool scripts)
    {
        Assert.SkipUnless(Master is not null && Replica is not null, "Set REDIS_CONNECTION_STRING and CLAIM_REPLICA_CONNECTION_STRING to run.");
        await using var first = await ConnectionMultiplexer.ConnectAsync($"{Master},allowAdmin=true");
        await using var fresh = await ConnectionMultiplexer.ConnectAsync($"{Master},allowAdmin=true,syncTimeout=10000");
        await using var replica = await ConnectionMultiplexer.ConnectAsync($"{Replica},allowAdmin=true,syncTimeout=10000");
        var key = (RedisKey)$"claimtest:mx:{{{Guid.NewGuid():N}}}";
        try
        {
            (await new ClaimStore(first.GetDatabase(), key, scripts, new Version(7, 4)).OpenAsync("rt:a", T1, "rt", restore: true)).Durability.Should().Be(ClaimDurability.Confirmed);
            var sleeping = replica.GetServer(replica.GetEndPoints()[0]).ExecuteAsync("DEBUG", "SLEEP", "2");
            await Task.Delay(100, TestContext.Current.CancellationToken);

            // The fresh connection never wrote: without the rewrite of the repeat, its WAIT would return at once with the replica's count.
            var repeat = await new ClaimStore(fresh.GetDatabase(), key, scripts, new Version(7, 4)) { BarrierTimeout = TimeSpan.FromMilliseconds(200), BarrierRetries = 1 }
                .OpenAsync("rt:a", T1, "rt", restore: true);

            repeat.Should().Be(new ClaimResult(2, ClaimDurability.NotConfirmed));
            await sleeping;
        }
        finally
        {
            await first.GetDatabase().KeyDeleteAsync(key);
        }
    }

    [Fact]
    public async Task CertifyClaim_reports_a_step_that_wait_did_not_confirm()
    {
        Assert.SkipUnless(Master is not null && Replica is not null, "Set REDIS_CONNECTION_STRING and CLAIM_REPLICA_CONNECTION_STRING to run.");
        await using var master = await ConnectionMultiplexer.ConnectAsync($"{Master},allowAdmin=true,syncTimeout=10000");
        await using var replica = await ConnectionMultiplexer.ConnectAsync($"{Replica},allowAdmin=true,syncTimeout=10000");
        var db = master.GetDatabase();
        var key = (RedisKey)$"claimtest:mx:{{{Guid.NewGuid():N}}}";
        try
        {
            await db.HashSetAsync(key, "rt:r", $"open|{T1}|rt|");
            var store = new ClaimStore(db, key, useScripts: true, new Version(7, 4)) { BarrierTimeout = TimeSpan.FromMilliseconds(200), BarrierRetries = 1 };
            var sleeping = replica.GetServer(replica.GetEndPoints()[0]).ExecuteAsync("DEBUG", "SLEEP", "2");
            await Task.Delay(100, TestContext.Current.CancellationToken);

            var outcome = await new ClaimOperator(store, new TerminalEvidence()).CertifyAsync("rt:r", $"open|{T1}|rt|", "n", TestContext.Current.CancellationToken);

            outcome.Should().Be(ClaimCommandOutcome.DoneUnconfirmed, "the caller raises wait_skipped{claim}");
            await sleeping;
        }
        finally
        {
            await db.KeyDeleteAsync(key);
        }
    }

    [Fact]
    public async Task A_reconnect_between_the_write_and_the_wait_is_caught_by_the_server_and_the_write_is_sent_again()
    {
        Assert.SkipUnless(Master is not null && Replica is not null, "Set REDIS_CONNECTION_STRING and CLAIM_REPLICA_CONNECTION_STRING to run.");
        await using var claim = await ConnectionMultiplexer.ConnectAsync($"{Master},allowAdmin=true,syncTimeout=10000");
        await using var admin = await ConnectionMultiplexer.ConnectAsync($"{Master},allowAdmin=true");
        var key = (RedisKey)$"claimtest:mx:{{{Guid.NewGuid():N}}}";
        var real = claim.GetServer(claim.GetEndPoints()[0]);
        var waits = 0;

        // The first WAIT runs only after its connection was killed and replaced: on the new connection it would count a vacuous offset.
        var server = InterceptingProxy.Wrap(real, (method, args) =>
            method.Name == nameof(IServer.ExecuteAsync) && args[0] is "WAIT" && Interlocked.Increment(ref waits) == 1 ? KillThenWaitAsync(real, admin, (object[])args[1]!) : InterceptingProxy.Proceed);
        var mux = InterceptingProxy.Wrap<IConnectionMultiplexer>(claim, (method, _) => method.Name == nameof(IConnectionMultiplexer.GetServer) ? server : InterceptingProxy.Proceed);
        var db = InterceptingProxy.Wrap(claim.GetDatabase(), (method, _) => method.Name == "get_Multiplexer" ? mux : InterceptingProxy.Proceed);
        try
        {
            var result = await new ClaimStore(db, key, useScripts: true, new Version(7, 4)).OpenAsync("rt:a", T1, "rt", restore: true);

            result.Should().Be(new ClaimResult(1, ClaimDurability.Confirmed));
            waits.Should().Be(2, "the first WAIT ran on another connection than the write, so the write and WAIT ran again");
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

    private sealed class TerminalEvidence : IClaimEvidence
    {
        public Task<ClaimTerminal?> ReadTerminalAsync(string group, CancellationToken cancellationToken) => Task.FromResult<ClaimTerminal?>(new ClaimTerminal(3, Swept: false));

        public Task<bool> ShowsTerminalAsync(string group, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> RemovableAsync(string group, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
