using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests;

public class FillWatchesTests
{
    [Fact]
    public void A_watch_is_intact_until_its_key_changes()
    {
        var watches = new FillWatches();
        var token = watches.Begin("k");

        watches.Intact("k", token).Should().BeTrue();
        watches.Changed("k");

        watches.Intact("k", token).Should().BeFalse();
    }

    [Fact]
    public void A_change_of_another_key_leaves_the_watch_intact()
    {
        var watches = new FillWatches();
        var token = watches.Begin("k");

        watches.Changed("other");

        watches.Intact("k", token).Should().BeTrue();
    }

    [Fact]
    public void Fills_of_one_key_share_a_watch_until_the_last_ends()
    {
        var watches = new FillWatches();
        var first = watches.Begin("k");
        var second = watches.Begin("k");
        first.Should().Be(second);

        watches.End("k");
        watches.Intact("k", second).Should().BeTrue("the other fill is still in progress");
        watches.End("k");

        watches.Any.Should().BeFalse();
        watches.Intact("k", second).Should().BeFalse("a key nobody watches stands for no fill");
    }

    [Fact]
    public void A_token_of_a_finished_watch_is_never_intact_in_a_later_one()
    {
        var watches = new FillWatches();
        var old = watches.Begin("k");
        watches.End("k");
        var current = watches.Begin("k");

        watches.Intact("k", old).Should().BeFalse();
        watches.Intact("k", current).Should().BeTrue();
    }

    [Fact]
    public void A_change_of_every_key_passes_every_token_taken_before_it_and_none_taken_after()
    {
        var watches = new FillWatches();
        var tokens = Enumerable.Range(0, 200).Select(i => (Key: $"k{i}", Token: watches.Begin($"k{i}"))).ToArray();

        watches.ChangedAll();
        var later = watches.Begin("k0");

        tokens.Should().OnlyContain(t => !watches.Intact(t.Key, t.Token));
        watches.Intact("k0", later).Should().BeTrue();
    }

    [Fact]
    public void A_change_with_no_watch_in_progress_is_a_no_op_and_ends_nothing()
    {
        var watches = new FillWatches();

        watches.Changed("k");
        watches.ChangedAll();
        watches.End("k");

        watches.Any.Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_changes_and_fills_of_many_keys_end_with_no_watch_left()
    {
        var watches = new FillWatches();
        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(
            () =>
            {
                for (var i = 0; i < 2000; i++)
                {
                    var key = $"k{(i + t) % 50}";
                    var token = watches.Begin(key);
                    watches.Changed(key);
                    watches.Changed($"x{i}");
                    watches.Intact(key, token).Should().BeFalse();
                    watches.End(key);
                }

                watches.ChangedAll();
            },
            TestContext.Current.CancellationToken));

        await Task.WhenAll(tasks);

        watches.Any.Should().BeFalse();
    }
}

/// <summary>Which writes of a key a fill in progress for it treats as a change.</summary>
public class FillWatchWritesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_awaited_write_supersedes_the_fill_and_a_fills_own_write_does_not()
    {
        var writes = Writes();
        var token = writes.BeginFill("k");

        await writes.FillAsync("k", 0, static (_, _) => new ValueTask<bool>(true), static (_, written, _) => written, Ct);
        writes.FillIntact("k", token).Should().BeTrue("a fill is not a change of the key");

        // Set, Remove and TryAdd all write through here.
        await writes.WriteAsync("k", 0, null, static (_, _) => new ValueTask<bool>(true), static (_, written, _) => written, Ct);
        writes.FillIntact("k", token).Should().BeFalse();
    }

    [Fact]
    public async Task A_write_of_many_keys_supersedes_the_fills_of_those_keys_only()
    {
        var writes = Writes();
        var (first, second, other) = (writes.BeginFill("a"), writes.BeginFill("b"), writes.BeginFill("c"));

        await writes.WriteAsync(["a", "b"], 0, null, static (_, _) => new ValueTask<bool>(true), static (_, written, _) => written, Ct);

        writes.FillIntact("a", first).Should().BeFalse();
        writes.FillIntact("b", second).Should().BeFalse();
        writes.FillIntact("c", other).Should().BeTrue();
    }

    [Fact]
    public async Task A_background_write_supersedes_the_fill_and_a_background_fill_does_not()
    {
        var writes = Writes();
        writes.Activate();
        var token = writes.BeginFill("k");

        writes.RunWithoutEvent("k", 0, static _ => true, static (_, _) => ValueTask.CompletedTask);
        writes.FillIntact("k", token).Should().BeTrue("a background fill sends no event and is not a change");

        writes.Run("k", 0, static _ => true, static (_, _) => ValueTask.CompletedTask);
        writes.FillIntact("k", token).Should().BeFalse();
        await writes.DrainAsync();
    }

    private static BackgroundWrites Writes() => new(8, TimeSpan.FromSeconds(5), "c", NullLogger.Instance, Substitute.For<ICachingTelemetryProvider>(), TimeProvider.System);
}
/// <summary>A write or event that nobody fills for allocates nothing, with a fill of another key in progress.</summary>
[Collection(AllocationCollection.Name)]
public class FillWatchesAllocationTests
{
    [Fact]
    public void Changes_and_batch_entries_allocate_nothing_while_other_keys_are_being_filled()
    {
        var writes = new BackgroundWrites(0, TimeSpan.FromSeconds(5), "c", NullLogger.Instance, Substitute.For<ICachingTelemetryProvider>(), TimeProvider.System);
        writes.BeginFill("filled");
        string[] keys = ["a", "b", "c"];
        writes.Enter(keys);
        writes.NoteChange("a");
        writes.Heard("a");

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            writes.NoteChange("a");
            writes.Enter(keys);
            writes.Enter("a").Dispose();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().BeLessThan(1000 * 64, "a write of a key nobody fills takes no allocation of its own");
    }

    [Fact]
    public void A_batch_entry_with_nothing_to_watch_does_not_copy_its_keys()
    {
        var writes = new BackgroundWrites(0, TimeSpan.FromSeconds(5), "c", NullLogger.Instance, Substitute.For<ICachingTelemetryProvider>(), TimeProvider.System);
        IEnumerable<string> keys = new List<string> { "a", "b", "c" };
        writes.Enter(keys);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            writes.Enter(keys);
        }

        // A list is enumerated into an array only when counting or a fill needs the keys; the list's own boxed enumerator is not touched either.
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(1000, "no fill is in progress and nothing counts");
    }
}
