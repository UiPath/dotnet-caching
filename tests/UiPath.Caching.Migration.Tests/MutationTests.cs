using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class MutationTests : RouterTestBase
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_write_commits_in_redis_then_evicts_locally_then_publishes(bool library)
    {
        var node = NewNode(useLibrary: _ => library);
        Store["k"] = new Item("old");
        await node.Cache.TryGetAsync("k", Ct);
        node.IsLocal("k").Should().BeTrue();
        var seenAtPublish = new List<string>();
        Bus.OnPublish = _ => seenAtPublish.Add($"{(Store["k"].Name)}/{(node.IsLocal("k") ? "cached" : "evicted")}");

        var outcome = await node.Cache.SetAsync("k", new Item("new"), default, Ct);

        outcome.Should().Be(WriteOutcome.Committed);
        seenAtPublish.Should().Equal("new/evicted");
    }

    [Fact]
    public async Task A_remove_is_evicted_and_published_after_the_command()
    {
        var node = NewNode();
        Store["k"] = new Item("old");
        await node.Cache.TryGetAsync("k", Ct);
        var seen = new List<string>();
        Bus.OnPublish = ev => seen.Add($"{ev.Type}/{Store.ContainsKey("k")}/{node.IsLocal("k")}");

        await node.Cache.RemoveAsync("k", Ct);

        seen.Should().Equal("CacheRemoved/False/False");
    }

    [Fact]
    public async Task An_unknown_outcome_evicts_and_publishes_at_once_and_keeps_the_key_off_the_local_tier_until_the_window_passes()
    {
        var node = NewNode(pendingWindow: Window);
        Store["k"] = new Item("old");
        await node.Cache.TryGetAsync("k", Ct);
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;

        var outcome = await node.Cache.SetAsync("k", new Item("new"), default, Ct);

        outcome.Should().Be(WriteOutcome.Unknown);
        node.IsLocal("k").Should().BeFalse();
        Bus.Published.Should().Be(1);
        node.LibraryBackend.WriteOutcome = WriteOutcome.Committed;
        await node.Cache.TryGetAsync("k", Ct);
        await node.Cache.TryGetAsync("k", Ct);
        node.IsLocal("k").Should().BeFalse("reads go to Redis while the mutation is pending");
        node.LibraryBackend.Reads.Should().Be(3);

        await RunBackgroundAsync(node, Window);

        Bus.Published.Should().Be(2, "the second eviction is published again");
        await node.Cache.TryGetAsync("k", Ct);
        node.IsLocal("k").Should().BeTrue("the pending mark is gone");
    }

    [Fact]
    public async Task A_late_commit_of_an_unknown_write_is_followed_by_a_second_eviction_on_every_node()
    {
        var writer = NewNode(pendingWindow: Window);
        var peer = NewNode();
        Store["k"] = new Item("old");
        writer.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        await writer.Cache.SetAsync("k", new Item("new"), default, Ct);

        (await peer.Cache.TryGetAsync("k", Ct)).Should().Be(new Item("old"), "the command has not landed yet");
        peer.IsLocal("k").Should().BeTrue();
        writer.LibraryBackend.Land("k", new Item("new"));
        await RunBackgroundAsync(writer, Window);

        peer.IsLocal("k").Should().BeFalse("the event after the late commit evicts what the peer read before it");
        (await peer.Cache.TryGetAsync("k", Ct)).Should().Be(new Item("new"));
    }

    [Fact]
    public async Task A_failed_publish_is_counted_and_retried_with_backoff_until_it_succeeds()
    {
        using var meters = new MeterCollector(CacheName);
        var writer = NewNode(localCap: TimeSpan.FromSeconds(30));
        var peer = NewNode();
        Store["k"] = new Item("old");
        await peer.Cache.TryGetAsync("k", Ct);
        Bus.FailPublish = true;

        await writer.Cache.SetAsync("k", new Item("new"), default, Ct);

        peer.IsLocal("k").Should().BeTrue("the event never reached the peer");
        meters.Count("publish_failed").Should().Be(1);
        Bus.FailPublish = false;
        await RunBackgroundAsync(writer, TimeSpan.FromSeconds(1));

        peer.IsLocal("k").Should().BeFalse("a retry delivered the event");
    }

    [Fact]
    public async Task Publish_retries_stop_at_the_local_cap()
    {
        using var meters = new MeterCollector(CacheName);
        var writer = NewNode(localCap: TimeSpan.FromSeconds(2));
        Bus.FailPublish = true;

        await writer.Cache.SetAsync("k", new Item("v"), default, Ct);
        await RunBackgroundAsync(writer, TimeSpan.FromSeconds(1));

        writer.Core.IsIdle.Should().BeTrue();
        meters.Count("publish_failed").Should().BeInRange(2, 6);
    }

    [Fact]
    public async Task A_deadline_already_passed_is_a_delete()
    {
        var node = NewNode();
        Store["k"] = new Item("old");

        await node.Cache.SetAsync("k", new Item("new"), CacheExpiry.At(Clock.GetUtcNow().AddSeconds(-1)), Ct);

        node.LibraryBackend.Writes.Should().Be(0);
        node.LibraryBackend.Removes.Should().Be(1);
        Store.Should().NotContainKey("k");
    }

    [Fact]
    public async Task A_relative_expiry_becomes_one_absolute_deadline_and_the_default_applies_when_none_is_given()
    {
        var node = NewNode(defaultExpiry: TimeSpan.FromMinutes(5));

        await node.Cache.SetAsync("a", new Item("1"), CacheExpiry.After(TimeSpan.FromSeconds(10)), Ct);
        node.LibraryBackend.LastDeadline.Should().Be(Clock.GetUtcNow().AddSeconds(10));
        await node.Cache.SetAsync("b", new Item("2"), default, Ct);
        node.LibraryBackend.LastDeadline.Should().Be(Clock.GetUtcNow().AddMinutes(5));
    }

    [Fact]
    public async Task Clear_publishes_clears_the_source_and_publishes_again()
    {
        var node = NewNode();
        var peer = NewNode();
        Store["k"] = new Item("v");
        await peer.Cache.TryGetAsync("k", Ct);
        var order = new List<string>();
        Bus.OnPublish = ev => order.Add($"publish:{Store.Count}");
        node.SourceBackend.BeforeClear = () =>
        {
            order.Add("clear");
            return Task.CompletedTask;
        };

        await node.Cache.ClearAsync(Ct);

        order.Should().Equal("publish:1", "clear", "publish:0");
        peer.IsLocal("k").Should().BeFalse();
    }

    [Fact]
    public async Task A_clear_that_fails_still_publishes_again_and_fails_the_call()
    {
        var node = NewNode();
        node.SourceBackend.ClearFails = new IOException("scan failed");

        var clear = async () => await node.Cache.ClearAsync(Ct);

        await clear.Should().ThrowAsync<IOException>();
        Bus.Published.Should().Be(2);
    }
}
