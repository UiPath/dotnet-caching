using StackExchange.Redis;
using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

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
        await using var master = await ConnectionMultiplexer.ConnectAsync(Master!);
        await using var replica = await ConnectionMultiplexer.ConnectAsync($"{Replica},allowAdmin=true,syncTimeout=10000");
        var db = master.GetDatabase();
        var key = (RedisKey)$"claimtest:mx:{{{Guid.NewGuid():N}}}";
        try
        {
            var store = new ClaimStore(db, key, useScripts: true, new Version(7, 4)) { BarrierTimeout = TimeSpan.FromMilliseconds(200), BarrierRetries = 2 };
            (await store.OpenAsync("rt:a", T1, "rt", restore: true)).Durability.Should().Be(ClaimDurability.Confirmed);

            // The replica sleeps, so no acknowledgement arrives while the step is retried.
            var sleeping = replica.GetServer(replica.GetEndPoints()[0]).ExecuteAsync("DEBUG", "SLEEP", "2");
            await Task.Delay(100);

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
}
