using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A slot that moves to another master while keys of the group move: the slot is split between two masters for a while, and every value read
// after the move is the newest one acknowledged.
[Collection("RedisIntegration")]
[Trait("Category", "Chaos")]
public sealed class KeyMoveTopologyChangeTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task A_write_during_a_split_source_slot_reports_unknown_and_the_destination_holds_the_settled_value(bool scripts, bool noOrigin)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var cluster = await ClusterKit.CreateAsync();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: noOrigin, cluster: true, clock: clock);
        var key = Enumerable.Range(0, HashSlot.Count).Select(i => $"k{i}")
            .First(k => kit.Mux.HashSlot(kit.L(k)) != kit.Mux.HashSlot(kit.N(k)));
        var source = kit.L(key);
        var destination = kit.N(key);
        await kit.PrepareAsync(key);
        await kit.SetStateAsync("S1");
        (await kit.Client.SetAsync(key, new Item("v0"), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        await kit.SetStateAsync("S3");

        // Moving the control record first prevents the source delete in both Redis forms.
        var migration = await cluster.BeginMigrationAsync(kit.Mux.HashSlot(source), kit.Names.ControlFor(kit.Group, source));
        var during = await kit.Client.ReadAsync(key, Ct);
        if (noOrigin)
        {
            // Redis checks the marked write's deadline against the destination master's clock.
            clock.Advance(await cluster.MasterOf(destination).TimeAsync() - DateTime.UnixEpoch);
        }

        var written = await kit.Client.SetAsync(key, new Item("v1"), null, Ct);
        var settlement = kit.Client.SettledAsync(key);

        await cluster.FinishMigrationAsync(migration);
        await kit.Mux.ConfigureAsync();
        written.Outcome.Should().Be(WriteOutcome.Unknown);
        during.Value!.Name.Should().Be("v0");
        settlement.IsCompleted.Should().Be(!noOrigin, "only the destination-first write has a trailing step to retry");

        clock.Advance(kit.Client.Parts.Options.RetryInitial);
        await kit.Client.Retries.RunDueAsync();
        settlement.IsCompletedSuccessfully.Should().BeTrue();

        if (noOrigin)
        {
            (await kit.Db.KeyExistsAsync(source, CommandFlags.DemandMaster)).Should().BeFalse("the retry completes the source delete");
        }
        else
        {
            (await kit.Client.ReadAsync(key, Ct)).Value!.Name.Should().Be("v0");
        }

        ((string?)await kit.Db.StringGetAsync(destination, CommandFlags.DemandMaster)).Should().Be(noOrigin ? "v1" : "v0");

        (await kit.Client.SetAsync(key, new Item("v2"), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        ((string?)await kit.Db.StringGetAsync(destination, CommandFlags.DemandMaster)).Should().Be("v2");
        (await kit.Db.KeyExistsAsync(source, CommandFlags.DemandMaster)).Should().BeFalse();
    }

    [Fact]
    public async Task Disposing_a_cluster_kit_after_a_failure_finishes_the_open_slot_migration()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var kit = await KeyMoveKit.CreateAsync(fixture, cluster: true, clock: clock);
        await kit.PrepareAsync("k");
        var source = kit.L("k");
        await kit.Db.StringSetAsync(source, "v0");
        var slot = kit.Mux.HashSlot(source);
        var before = ShardFailingDatabase.OwnerOf(kit.Mux, source);
        var failure = new InvalidOperationException("Interrupted slot migration.");
        var cluster = await ClusterKit.CreateAsync();

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var interrupted = cluster;
            await interrupted.BeginMigrationAsync(slot, kit.Names.ControlFor(kit.Group, source));
            clock.Advance(TimeSpan.FromSeconds(1));
            throw failure;
        });

        caught.Should().BeSameAs(failure);
        await using var next = await ClusterKit.CreateAsync();
        next.MasterOf(source).EndPoint.Should().NotBe(before);
        ((string?)await next.Mux.GetDatabase().StringGetAsync(source, CommandFlags.DemandMaster)).Should().Be("v0");
    }

    [Fact]
    public async Task A_promotion_after_the_slot_of_N_moved_publishes_under_the_fence_on_its_new_master()
    {
        await using var cluster = await ClusterKit.CreateAsync();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, cluster: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        var slot = kit.Mux.HashSlot(kit.N("k"));
        var before = ShardFailingDatabase.OwnerOf(kit.Mux, kit.N("k"));

        await cluster.FinishMigrationAsync(await cluster.BeginMigrationAsync(slot));
        await kit.Mux.ConfigureAsync();

        ShardFailingDatabase.OwnerOf(kit.Mux, kit.N("k")).Should().NotBe(before);
        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("v0");
        (await kit.GetAsync(kit.N("k"))).Should().Be("v0");
        (await kit.FenceAsync("k")).Should().NotBeNull();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }
}
