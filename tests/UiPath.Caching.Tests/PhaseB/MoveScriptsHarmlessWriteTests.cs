using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

// Serialized: the count of HSET calls is server wide, so no other test may write a hash meanwhile.
[Collection(RedisAllocationCollection.Name)]
[Trait("Category", "Integration")]
public class MoveScriptsHarmlessWriteTests(RedisContainerFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_repeat_of_ctlsync_ctlcommit_and_gate_still_writes_so_a_wait_has_an_offset(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, allowAdmin: true);
        var server = env.Mux.GetServer(env.Mux.GetEndPoints()[0]);

        var before = await HsetCallsAsync(server);
        (await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(1, MoveTestEnvironment.Id, (MoveTestEnvironment.Cid, "n")))).Should().Be(0);
        var afterSync = await HsetCallsAsync(server);
        afterSync.Should().BeGreaterThan(before);

        (await env.Scripts.ControlCommitAsync(env.Ctl, 1, MoveTestEnvironment.Id)).Should().Be(1);
        var committed = await HsetCallsAsync(server);
        (await env.Scripts.ControlCommitAsync(env.Ctl, 1, MoveTestEnvironment.Id)).Should().Be(1);
        var afterCommit = await HsetCallsAsync(server);
        afterCommit.Should().BeGreaterThan(committed);

        (await env.Scripts.GateAsync(env.Ctl, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "CLOSED:x")).Should().Be(1);
        var gated = await HsetCallsAsync(server);
        (await env.Scripts.GateAsync(env.Ctl, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "CLOSED:x")).Should().Be(1);
        (await HsetCallsAsync(server)).Should().BeGreaterThan(gated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_older_ctlsync_writes_nothing(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, allowAdmin: true);
        var server = env.Mux.GetServer(env.Mux.GetEndPoints()[0]);
        var before = await HsetCallsAsync(server);

        (await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(0, "id0", (MoveTestEnvironment.Cid, "n")))).Should().Be(0);

        (await HsetCallsAsync(server)).Should().Be(before);
    }

    private static async Task<long> HsetCallsAsync(IServer server)
    {
        var stats = await server.InfoAsync("commandstats");
        var line = stats.SelectMany(g => g).FirstOrDefault(p => p.Key == "cmdstat_hset").Value;
        line.Should().NotBeNullOrEmpty();
        return long.Parse(line.Split(',')[0].Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);
    }
}
