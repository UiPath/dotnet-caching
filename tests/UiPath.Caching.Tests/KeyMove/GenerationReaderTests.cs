using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The generation reader: run id and replication id of a master from a plain INFO, and unknown, never unchanged, for anything it cannot show.</summary>
[Collection("TwoServers")]
[Trait("Category", "Integration")]
public sealed class GenerationReaderTests(TwoServerFixture fixture)
{
    [Fact]
    public async Task A_master_reads_as_its_run_id_and_replication_id_and_reads_the_same_until_it_changes()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1, TWO_SERVER_SRC and TWO_SERVER_DST to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{TwoServerFixture.Dst},allowAdmin=true");
        var reader = new GenerationReader(_ => mux);
        var info = (string?)await mux.GetServer(mux.GetEndPoints()[0]).ExecuteAsync("INFO");

        var first = await reader.ReadAsync(RecordSide.Dst, TestContext.Current.CancellationToken);
        var second = await reader.ReadAsync(RecordSide.Dst, TestContext.Current.CancellationToken);

        first.Should().Be(new ServerGeneration(ConnectionProof.InfoField(info, "run_id:")!, ConnectionProof.InfoField(info, "master_replid:")!));
        second.Should().Be(first);
    }

    [Fact]
    public async Task The_two_servers_have_generations_of_their_own_and_a_pair_needs_both()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        IGenerationSource reader = kit.Conns.Generations();
        IGenerationSource scripted = kit.Generations;

        var pair = await reader.ReadPairAsync(TestContext.Current.CancellationToken);

        pair.Should().NotBeNull();
        pair.Value.Src.Should().NotBe(pair.Value.Dst);
        kit.Generations.Unknown(RecordSide.Src, true);
        (await scripted.ReadPairAsync(TestContext.Current.CancellationToken)).Should().BeNull();
        kit.Generations.Unknown(RecordSide.Src, false);
        kit.Generations.Unknown(RecordSide.Dst, true);
        (await scripted.ReadPairAsync(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task A_server_that_is_not_connected_is_unknown()
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:1,abortConnect=false,connectTimeout=500,allowAdmin=true");
        var reader = new GenerationReader(_ => mux);

        (await reader.ReadAsync(RecordSide.Dst, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task A_connection_that_knows_only_a_replica_is_unknown()
    {
        var replica = Environment.GetEnvironmentVariable("CLAIM_REPLICA_CONNECTION_STRING");
        Assert.SkipUnless(fixture.Enabled && replica is not null, "Set CLAIM_REPLICA_CONNECTION_STRING to a replica to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{replica},allowAdmin=true");
        var reader = new GenerationReader(_ => mux);

        (await reader.ReadAsync(RecordSide.Dst, TestContext.Current.CancellationToken)).Should().BeNull();
    }
}
