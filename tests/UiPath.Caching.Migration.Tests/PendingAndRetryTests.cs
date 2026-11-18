using UiPath.Caching.Broadcast;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class PendingAndRetryTests : RouterTestBase
{
    [Fact]
    public async Task An_adapter_that_names_a_longer_command_lifetime_than_the_window_extends_the_pending_time()
    {
        var node = NewNode(pendingWindow: TimeSpan.FromSeconds(10));
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        node.LibraryBackend.SettleWithin = TimeSpan.FromSeconds(60);
        await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        await WaitForTimerAsync();
        Clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100, Ct);

        Bus.Published.Should().Be(1, "the command can still land for another 30 s");
        await RunBackgroundAsync(node, TimeSpan.FromSeconds(31));
        Bus.Published.Should().Be(2);
    }

    [Fact]
    public async Task A_shorter_settle_deadline_does_not_shorten_the_window()
    {
        var node = NewNode(pendingWindow: TimeSpan.FromSeconds(30));
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        node.LibraryBackend.SettleWithin = TimeSpan.FromSeconds(1);
        await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        await WaitForTimerAsync();
        Clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(100, Ct);

        Bus.Published.Should().Be(1);
        await RunBackgroundAsync(node, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Many_unknown_writes_to_one_key_share_one_timer()
    {
        var node = NewNode(pendingWindow: TimeSpan.FromSeconds(30));
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;

        for (var write = 0; write < 50; write++)
        {
            await node.Cache.SetAsync("k", new Item($"v{write}"), default, Ct);
        }

        node.Core.BackgroundCount.Should().Be(1);
        await RunBackgroundAsync(node, TimeSpan.FromSeconds(31));
        Bus.Published.Should().Be(51, "fifty announcements and one final one");
    }

    [Fact]
    public async Task Failed_publishes_of_one_key_are_kept_once_and_run_by_one_worker()
    {
        var node = NewNode(localCap: TimeSpan.FromSeconds(30));
        Bus.FailPublish = true;

        for (var write = 0; write < 20; write++)
        {
            await node.Cache.SetAsync("k", new Item($"v{write}"), default, Ct);
        }

        node.Cache.Retrier.WaitingKeys.Should().Be(1);
        node.Core.BackgroundCount.Should().Be(1);
        Bus.FailPublish = false;
        await RunBackgroundAsync(node, TimeSpan.FromSeconds(1));
        Bus.Published.Should().Be(1, "one announcement for the key, whatever the number of writes");
    }

    [Fact]
    public async Task Too_many_failed_keys_fall_back_to_one_topic_invalidation_when_publishing_recovers()
    {
        var node = NewNode(localCap: TimeSpan.FromSeconds(30));
        var peer = NewNode();
        Store["a"] = new Item("v");
        await peer.Cache.TryGetAsync("a", Ct);
        Bus.FailPublish = true;

        for (var key = 0; key < InvalidationRetrier.MaxKeys + 5; key++)
        {
            await node.Cache.SetAsync($"key{key}", new Item("v"), default, Ct);
        }

        node.Cache.Retrier.ClearWaiting.Should().BeTrue();
        node.Cache.Retrier.WaitingKeys.Should().Be(0);
        Bus.FailPublish = false;
        await RunBackgroundAsync(node, TimeSpan.FromSeconds(1));

        Bus.Events.Should().ContainSingle(ev => ev.Type == KnownEventTypes.TopicInvalidated);
        Bus.Published.Should().Be(1);
        peer.IsLocal("a").Should().BeFalse("the invalidation cleared the peer's cache");
    }

    [Fact]
    public async Task Time_spent_failing_counts_against_the_cap()
    {
        var node = NewNode(localCap: TimeSpan.FromSeconds(3));
        Bus.FailPublish = true;
        await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        await RunBackgroundAsync(node, TimeSpan.FromSeconds(1));
        Bus.FailPublish = false;

        node.Core.IsIdle.Should().BeTrue();
        Bus.Published.Should().Be(0, "the entry outlived the cap, so a late publish would protect nothing");
    }

    private async Task WaitForTimerAsync()
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!Clock.HasTimers && DateTime.UtcNow < until)
        {
            await Task.Delay(5, Ct);
        }
    }
}
