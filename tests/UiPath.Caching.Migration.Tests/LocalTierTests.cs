using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class LocalTierTests : RouterTestBase
{
    [Fact]
    public async Task A_fill_started_before_an_eviction_of_its_key_is_not_installed()
    {
        var node = NewNode();
        Store["k"] = new Item("old");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.LibraryBackend.AfterRead = async () =>
        {
            reading.TrySetResult();
            await gate.Task;
        };

        var read = node.Cache.TryGetAsync("k", Ct).AsTask();
        await reading.Task;
        node.LibraryBackend.AfterRead = null;
        await node.SourceBackend.RemoveAsync("k", Ct);
        await node.Cache.RemoveAsync("k", Ct);
        gate.SetResult();
        (await read).Should().Be(new Item("old"), "the caller still gets what it read");

        node.IsLocal("k").Should().BeFalse("the removal came after the mark, so the stale fill is rejected");
    }

    [Fact]
    public async Task A_fill_started_before_a_clear_of_the_cache_is_not_installed()
    {
        var node = NewNode();
        Store["k"] = new Item("old");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.LibraryBackend.AfterRead = async () =>
        {
            reading.TrySetResult();
            await gate.Task;
        };

        var read = node.Cache.TryGetAsync("k", Ct).AsTask();
        await reading.Task;
        node.LibraryBackend.AfterRead = null;
        await node.Cache.ClearAsync(Ct);
        gate.SetResult();
        await read;

        node.IsLocal("k").Should().BeFalse();
    }

    [Fact]
    public async Task A_read_fills_the_local_tier_and_the_next_call_is_a_local_hit()
    {
        using var meters = new MeterCollector(CacheName);
        var node = NewNode();
        Store["k"] = new Item("v");

        await node.Cache.TryGetAsync("k", Ct);
        var again = await node.Cache.TryGetAsync("k", Ct);

        again.Should().Be(new Item("v"));
        node.LibraryBackend.Reads.Should().Be(1);
        meters.Count("local_hit").Should().Be(1);
        meters.Count("redis_hit").Should().Be(1);
    }

    [Fact]
    public async Task A_cap_of_zero_keeps_no_local_copy()
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        Store["k"] = new Item("v");

        await node.Cache.TryGetAsync("k", Ct);
        await node.Cache.TryGetAsync("k", Ct);

        node.LibraryBackend.Reads.Should().Be(2);
    }

    [Fact]
    public async Task The_local_deadline_is_the_read_start_plus_the_remaining_ttl_when_that_is_shorter_than_the_cap()
    {
        var node = NewNode(localCap: TimeSpan.FromMinutes(10));
        Store["k"] = new Item("v");
        node.LibraryBackend.ReportedTtl = TimeSpan.FromSeconds(5);
        await node.Cache.TryGetAsync("k", Ct);

        Clock.Advance(TimeSpan.FromSeconds(4));
        node.IsLocal("k").Should().BeTrue();
        Clock.Advance(TimeSpan.FromSeconds(2));

        node.IsLocal("k").Should().BeFalse("Redis would have expired the entry by now");
    }

    [Fact]
    public async Task A_read_of_an_entry_with_no_remaining_ttl_installs_nothing()
    {
        var node = NewNode();
        Store["k"] = new Item("v");
        node.LibraryBackend.ReportedTtl = TimeSpan.Zero;

        await node.Cache.TryGetAsync("k", Ct);

        node.IsLocal("k").Should().BeFalse();
    }

    [Fact]
    public async Task A_source_read_has_no_ttl_so_the_cap_alone_bounds_the_local_copy()
    {
        var node = NewNode(useLibrary: _ => false, localCap: TimeSpan.FromSeconds(30));
        Store["k"] = new Item("v");
        await node.Cache.TryGetAsync("k", Ct);

        Clock.Advance(TimeSpan.FromSeconds(29));
        node.IsLocal("k").Should().BeTrue();
        Clock.Advance(TimeSpan.FromSeconds(2));

        node.IsLocal("k").Should().BeFalse();
    }

    [Fact]
    public async Task Two_caches_of_one_node_keep_the_same_key_apart()
    {
        var other = $"other{Guid.NewGuid():N}"[..14];
        var first = NewNode();
        var second = NewNode(cacheName: other, sharing: first);
        Store["k"] = new Item("v");
        await first.Cache.TryGetAsync("k", Ct);

        first.IsLocal("k").Should().BeTrue();
        second.IsLocal("k").Should().BeFalse("one local tier holds both caches");
    }

    [Fact]
    public async Task A_cache_name_and_a_key_that_run_together_into_the_same_text_stay_two_entries()
    {
        var first = NewNode(cacheName: "a");
        var second = NewNode(cacheName: "a\u001Fb", sharing: first);
        Store["b\u001Fc"] = new Item("from-a");
        Store["c"] = new Item("from-a-b");
        await first.Cache.TryGetAsync("b\u001Fc", Ct);

        first.IsLocal("b\u001Fc").Should().BeTrue();
        second.IsLocal("c").Should().BeFalse("a different cache's key, though the joined text is the same");
        (await second.Cache.TryGetAsync("c", Ct)).Should().Be(new Item("from-a-b"));
    }
}
