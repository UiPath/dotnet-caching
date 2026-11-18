using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class OutcomeTests : RouterTestBase
{
    [Fact]
    public async Task A_library_that_is_unavailable_never_runs_the_factory_while_the_source_answers_with_a_hit()
    {
        using var meters = new MeterCollector(CacheName);
        var node = NewNode();
        Store["k"] = new Item("stored");
        node.LibraryBackend.ReadOverride = ReadOutcome.Unavailable;
        var factory = new Factory();

        var value = await node.Cache.GetOrAddAsync("k", factory.RunAsync, default, Ct);

        value.Should().Be(new Item("stored"));
        factory.Runs.Should().Be(0);
        meters.Count("unavailable").Should().Be(1);
        meters.Count("redis_hit", "path_reason", "other_client_read").Should().Be(1);
    }

    [Fact]
    public async Task A_library_and_a_source_that_are_both_unavailable_fail_the_call_without_running_the_factory()
    {
        var node = NewNode();
        node.LibraryBackend.ReadOverride = ReadOutcome.Unavailable;
        node.SourceBackend.ReadOverride = ReadOutcome.Unavailable;
        var factory = new Factory();

        var call = async () => await node.Cache.GetOrAddAsync("k", factory.RunAsync, default, Ct);

        await call.Should().ThrowAsync<CacheUnavailableException>();
        factory.Runs.Should().Be(0);
    }

    [Fact]
    public async Task A_library_that_is_unavailable_with_an_absent_source_entry_is_a_miss_and_the_write_goes_to_the_other_client()
    {
        var node = NewNode();
        node.LibraryBackend.ReadOverride = ReadOutcome.Unavailable;
        node.LibraryBackend.WriteOutcome = WriteOutcome.NotSent;

        var value = await node.Cache.GetOrAddAsync("k", _ => Made("made"), default, Ct);

        value.Should().Be(new Item("made"));
        node.SourceBackend.Writes.Should().Be(1, "the library never sent the write");
        Store["k"].Should().Be(new Item("made"));
    }

    [Fact]
    public async Task A_source_that_is_unavailable_is_a_miss_as_the_older_client_has_it()
    {
        var node = NewNode(useLibrary: _ => false);
        node.SourceBackend.ReadOverride = ReadOutcome.Unavailable;
        var factory = new Factory();

        var value = await node.Cache.GetOrAddAsync("k", factory.RunAsync, default, Ct);

        value.Should().Be(new Item("made"));
        factory.Runs.Should().Be(1);
        node.LibraryBackend.Reads.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_unreadable_entry_is_retried_on_the_other_client(bool library)
    {
        using var meters = new MeterCollector(CacheName);
        var node = NewNode(useLibrary: _ => library);
        Store["k"] = new Item("stored");
        (library ? node.LibraryBackend : node.SourceBackend).ReadOverride = ReadOutcome.Unreadable;

        var value = await node.Cache.GetOrAddAsync("k", _ => throw new InvalidOperationException("the other client reads it"), default, Ct);

        value.Should().Be(new Item("stored"));
        meters.Count("unreadable").Should().Be(1);
    }

    [Fact]
    public async Task An_entry_unreadable_on_both_clients_is_a_counted_miss_that_leaves_the_key_in_place()
    {
        using var meters = new MeterCollector(CacheName);
        var node = NewNode();
        Store["k"] = new Item("unreadable");
        node.LibraryBackend.ReadOverride = ReadOutcome.Unreadable;
        node.SourceBackend.ReadOverride = ReadOutcome.Unreadable;

        var value = await node.Cache.TryGetAsync("k", Ct);

        value.Should().BeNull();
        meters.Count("miss").Should().Be(1);
        meters.Count("unreadable", "path_reason", "other_client_unreadable").Should().Be(1);
        Store.Should().ContainKey("k");
        node.LibraryBackend.Removes.Should().Be(0);
        node.SourceBackend.Removes.Should().Be(0);
    }

    [Fact]
    public async Task A_write_the_library_never_sent_is_retried_on_the_source()
    {
        var node = NewNode();
        node.LibraryBackend.WriteOutcome = WriteOutcome.NotSent;

        var outcome = await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        outcome.Should().Be(WriteOutcome.Committed);
        node.LibraryBackend.Writes.Should().Be(1);
        node.SourceBackend.Writes.Should().Be(1);
    }

    [Fact]
    public async Task A_write_that_timed_out_after_sending_is_reported_and_never_replayed()
    {
        var node = NewNode();
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;

        var outcome = await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        outcome.Should().Be(WriteOutcome.Unknown);
        node.SourceBackend.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_remove_the_source_never_sent_is_retried_on_the_library_and_an_unknown_one_is_not()
    {
        var node = NewNode(useLibrary: _ => false);
        node.SourceBackend.WriteOutcome = WriteOutcome.NotSent;
        (await node.Cache.RemoveAsync("k", Ct)).Should().Be(WriteOutcome.Committed);
        node.LibraryBackend.Removes.Should().Be(1);

        node.SourceBackend.WriteOutcome = WriteOutcome.Unknown;
        (await node.Cache.RemoveAsync("k", Ct)).Should().Be(WriteOutcome.Unknown);
        node.LibraryBackend.Removes.Should().Be(1);
    }

    [Fact]
    public async Task A_write_neither_client_sent_changes_nothing_locally_and_publishes_nothing()
    {
        var node = NewNode();
        Store["k"] = new Item("old");
        await node.Cache.TryGetAsync("k", Ct);
        node.LibraryBackend.WriteOutcome = WriteOutcome.NotSent;
        node.SourceBackend.WriteOutcome = WriteOutcome.NotSent;

        var outcome = await node.Cache.SetAsync("k", new Item("new"), default, Ct);

        outcome.Should().Be(WriteOutcome.NotSent);
        node.IsLocal("k").Should().BeTrue();
        Bus.Published.Should().Be(0);
    }

    [Fact]
    public async Task A_factory_that_returns_null_is_returned_and_stored_nowhere()
    {
        var node = NewNode();

        var value = await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(null), default, Ct);

        value.Should().BeNull();
        node.LibraryBackend.Writes.Should().Be(0);
    }

    private static Task<Item?> Made(string name) => Task.FromResult<Item?>(new Item(name));

    private sealed class Factory
    {
        public int Runs { get; private set; }

        public Task<Item?> RunAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Runs++;
            return Made("made");
        }
    }
}
