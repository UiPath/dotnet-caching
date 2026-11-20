using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

public sealed class MultilayerMigrationWriteTests(ITestContextAccessor testContextAccessor) : IDisposable
{
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddMinutes(10);

    private readonly CachePeers _peers = new();

    private CancellationToken Ct => testContextAccessor.Current.CancellationToken;

    public void Dispose() => _peers.Dispose();

    [Fact]
    public async Task A_committed_write_and_delete_reach_the_store_and_are_announced()
    {
        var node = _peers.Add("a");
        var cache = node.Cache.AsMigrationCache();

        (await cache.WriteAsync("k", "v", Deadline, null, Ct)).Should().Be(StoreOutcome.Committed);
        _peers.Store.Values.Should().ContainKey("k");
        node.Published.Should().ContainSingle();
        (await cache.DeleteAsync<string>("k", Ct)).Should().Be(StoreOutcome.Committed);
        _peers.Store.Values.Should().NotContainKey("k");
        node.Published.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_write_and_delete_while_the_inner_tier_is_disconnected_are_not_sent()
    {
        var node = _peers.Add("a");
        node.Inner.IsConnected = false;
        var cache = node.Cache.AsMigrationCache();

        (await cache.WriteAsync("k", "v", Deadline, null, Ct)).Should().Be(StoreOutcome.NotSent);
        (await cache.DeleteAsync<string>("k", Ct)).Should().Be(StoreOutcome.NotSent);
        _peers.Store.Values.Should().BeEmpty();
        (await node.Cache.GetAsync<string>("k", Ct)).Should().BeNull("a refused write is not kept locally either");
    }

    [Theory]
    [InlineData(StoreOutcome.Unknown)]
    [InlineData(StoreOutcome.NotSent)]
    [InlineData(StoreOutcome.Failed)]
    public async Task The_outcome_of_a_write_and_delete_is_the_inner_tiers(StoreOutcome inner)
    {
        var node = _peers.Add("a");
        node.Inner.MigrationOutcome = inner;
        var cache = node.Cache.AsMigrationCache();

        (await cache.WriteAsync("k", "v", Deadline, null, Ct)).Should().Be(inner);
        (await cache.DeleteAsync<string>("k", Ct)).Should().Be(inner);
    }

    [Fact]
    public async Task A_write_under_a_background_policy_waits_for_the_store_and_reports_its_failure()
    {
        var node = _peers.Add("a");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Inner.MigrationGate = gate.Task;
        node.Inner.MigrationOutcome = StoreOutcome.Failed;
        var policy = new CachePolicy { BackgroundDistributedWrite = true };

        var writing = node.Cache.AsMigrationCache().WriteAsync("k", "v", Deadline, policy, Ct).AsTask();
        await Task.Delay(300, Ct);
        writing.IsCompleted.Should().BeFalse("the store has not answered, and a background acceptance is not a commit");
        gate.SetResult();

        (await writing).Should().Be(StoreOutcome.Failed);
    }

    [Fact]
    public async Task A_write_the_store_committed_stays_committed_when_its_announcement_fails()
    {
        var node = _peers.Add("a");
        node.PublishRefused = true;
        var cache = node.Cache.AsMigrationCache();

        (await cache.WriteAsync("k", "v", Deadline, null, Ct)).Should().Be(StoreOutcome.Committed);
        (await cache.DeleteAsync<string>("k", Ct)).Should().Be(StoreOutcome.Committed);
    }

    [Fact]
    public async Task A_failing_write_a_publisher_makes_on_the_way_does_not_change_the_outcome()
    {
        var node = _peers.Add("a");
        node.OnPublish = () =>
        {
            node.OnPublish = null;
            node.Inner.MigrationOutcome = StoreOutcome.Failed;
            node.Cache.SetAsync("unrelated", "x", Ct).AsTask().GetAwaiter().GetResult().Should().BeTrue("a plain write is not a migration write");
        };

        (await node.Cache.AsMigrationCache().WriteAsync("k", "v", Deadline, null, Ct)).Should().Be(StoreOutcome.Committed);
        _peers.Store.Values.Should().ContainKey("k");
    }

    [Fact]
    public async Task A_write_cancelled_before_it_starts_is_not_sent()
    {
        var node = _peers.Add("a");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        (await node.Cache.AsMigrationCache().WriteAsync("k", "v", Deadline, null, cts.Token)).Should().Be(StoreOutcome.NotSent);
        _peers.Store.Values.Should().BeEmpty();
    }
}
