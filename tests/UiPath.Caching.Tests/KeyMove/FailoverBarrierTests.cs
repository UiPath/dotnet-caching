using System.Net;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// Needs a primary of its own and a second server, which the test makes a copy of the primary and then an independent master; neither may be used by anything else:
// docker run -d --network host redis:7.4 redis-server --port 6396
// docker run -d --network host redis:7.4 redis-server --port 6397
// REDIS_FAILOVER_PRIMARY_CONNECTION_STRING=127.0.0.1:6396 REDIS_FAILOVER_REPLICA_CONNECTION_STRING=127.0.0.1:6397
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class FailoverBarrierTests(RedisContainerFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_master_demoted_resynced_and_promoted_on_a_surviving_connection_between_the_write_and_the_wait_is_not_confirmed(bool useScripts)
    {
        var (primary, other) = Servers();
        var options = new KeyMoveStateOptions { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromSeconds(2) };
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, options, connectionString: primary);
        await using var admin = await ConnectionMultiplexer.ConnectAsync(primary + ",allowAdmin=true");
        await using var spare = await ConnectionMultiplexer.ConnectAsync(other + ",allowAdmin=true");
        var spareEndpoint = spare.GetEndPoints()[0];
        var init = await kit.InitAsync("a");

        // The spare holds the initial state and is the master the primary will lose its write to; the primary keeps no replica, and the WAIT is answered with one, as after a failover.
        await CopyAndDetachAsync(spare, admin.GetEndPoints()[0], kit.StateKey);
        var waits = 0;
        kit.Topology.Wait = (_, _) => 1;
        kit.Topology.BeforeWait = async (endpoint, _) =>
        {
            if (Interlocked.Increment(ref waits) == 1)
            {
                await DemoteResyncAndPromoteAsync(admin.GetServer(endpoint), spareEndpoint);
            }
        };

        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed), "the WAIT of the TRANSIT ran on the same connection of the same process, which proves nothing about the write once the replication history changed");
        waits.Should().Be(6, "the barrier of the TRANSIT ran again, which rewrote what the resync had taken");
        kit.Topology.Opened.Should().Be(2);
        var stored = await kit.Store.ReadAsync(kit.Group, CancellationToken.None);
        stored!.Id.Should().Be(target.Id);
        stored.Durable.Should().BeTrue();
    }

    // The spare follows the primary until it holds the key, then stops following: two masters with the same data and different replication histories.
    private static async Task CopyAndDetachAsync(ConnectionMultiplexer spare, EndPoint primary, RedisKey key)
    {
        var server = spare.GetServer(spare.GetEndPoints()[0]);
        await server.ExecuteAsync("REPLICAOF", HostOf(primary), PortOf(primary));
        await WaitUntilAsync(async () => FieldOf((string)(await server.ExecuteAsync("INFO", "replication"))!, "master_link_status:") == "up" && await spare.GetDatabase().KeyExistsAsync(key));
        await server.ExecuteAsync("REPLICAOF", "NO", "ONE");
    }

    // The primary becomes a replica of the spare and takes its data, losing whatever it alone held, and then a master again: the client connections stay, the replication id is a new one.
    private static async Task DemoteResyncAndPromoteAsync(IServer primary, EndPoint spare)
    {
        await primary.ExecuteAsync("REPLICAOF", HostOf(spare), PortOf(spare));
        await WaitUntilAsync(async () =>
        {
            var info = (string)(await primary.ExecuteAsync("INFO", "replication"))!;
            return FieldOf(info, "master_link_status:") == "up" && FieldOf(info, "master_sync_in_progress:") == "0";
        });
        await primary.ExecuteAsync("REPLICAOF", "NO", "ONE");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The servers did not reach the state the test needs.");
    }

    private static string HostOf(EndPoint endpoint) => endpoint is IPEndPoint ip ? ip.Address.ToString() : ((DnsEndPoint)endpoint).Host;

    private static int PortOf(EndPoint endpoint) => endpoint is IPEndPoint ip ? ip.Port : ((DnsEndPoint)endpoint).Port;

    private static string? FieldOf(string info, string name) =>
        info.Split('\n').FirstOrDefault(l => l.StartsWith(name, StringComparison.Ordinal))?[name.Length..].Trim();

    private (string Primary, string Other) Servers()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var primary = Environment.GetEnvironmentVariable("REDIS_FAILOVER_PRIMARY_CONNECTION_STRING");
        var other = Environment.GetEnvironmentVariable("REDIS_FAILOVER_REPLICA_CONNECTION_STRING");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(primary) && !string.IsNullOrWhiteSpace(other), "Set REDIS_FAILOVER_PRIMARY_CONNECTION_STRING and REDIS_FAILOVER_REPLICA_CONNECTION_STRING to two throwaway servers that nothing else uses.");
        return (primary!, other!);
    }
}
