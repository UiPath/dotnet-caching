using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class PinTests : RouterTestBase
{
    private readonly FakePinStore _store = new();
    private bool _pinned;

    [Fact]
    public async Task A_pinned_group_takes_the_library_whatever_the_predicate_and_the_bypass_setting_say()
    {
        var node = NewNode(useLibrary: _ => false, bypass: () => true, pinWhen: _ => true, pinStore: _store);
        await Settled(node);
        Store["k"] = new Item("stored");

        var value = await node.Cache.TryGetAsync("k", Ct);

        value.Should().Be(new Item("stored"));
        node.LibraryBackend.Reads.Should().Be(1);
        node.SourceBackend.Reads.Should().Be(0);
    }

    [Fact]
    public async Task A_pinned_group_creates_on_the_library_whatever_the_predicate_and_the_bypass_setting_say()
    {
        var node = NewNode(useLibrary: _ => false, bypass: () => true, pinWhen: _ => true, pinStore: _store);
        await Settled(node);

        var outcome = await node.Cache.TryAddAsync("k", new Item("created"), default, Ct);

        outcome.Should().Be(AddOutcome.Added);
        node.LibraryBackend.Creates.Should().Be(1);
        node.SourceBackend.Creates.Should().Be(0);
    }

    [Fact]
    public async Task The_pin_is_acknowledged_only_after_a_create_that_started_on_the_source_has_ended()
    {
        var node = NewNode(useLibrary: _ => false, pinWhen: _ => _pinned, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);
        var release = new TaskCompletionSource();
        node.SourceBackend.InFlight = _ => release.Task;
        var inFlight = node.Cache.TryAddAsync("k", new Item("created"), default, Ct).AsTask();
        await UntilAsync(() => node.SourceBackend.Creates == 1);

        _pinned = true;
        pins.EvaluatePins();

        Tokens(pins).Should().NotContain("pin=g", "a create that started on the source is still running");

        node.SourceBackend.InFlight = null;
        release.SetResult();
        (await inFlight).Should().Be(AddOutcome.Added);
        pins.EvaluatePins();

        Tokens(pins).Should().Contain("pin=g");
    }

    [Fact]
    public async Task An_unknown_switch_state_fails_a_create_closed()
    {
        _store.Down = true;
        var node = NewNode(pinWhen: _ => false, pinStore: _store);
        var pins = node.Core.Pins!;
        pins.EvaluatePins();
        (await pins.RefreshAsync(Ct)).Should().BeFalse();

        var outcome = await node.Cache.TryAddAsync("k", new Item("created"), default, Ct);

        outcome.Should().Be(AddOutcome.NotSent);
        (node.SourceBackend.Creates, node.LibraryBackend.Creates).Should().Be((0, 0));
    }

    [Fact]
    public async Task A_pinned_group_whose_library_has_not_started_fails_unavailable_and_reaches_neither_the_source_nor_the_factory()
    {
        var node = NewNode(pinWhen: _ => true, pinStore: _store, lazyLibraryAsync: _ => throw new InvalidOperationException("no library"));
        var made = 0;

        var read = async () => await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("made" + made++)), default, Ct);
        var write = await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        await read.Should().ThrowAsync<CacheUnavailableException>();
        write.Should().Be(WriteOutcome.NotSent);
        made.Should().Be(0, "an unavailable answer is not a miss");
        node.SourceBackend.Reads.Should().Be(0);
        node.SourceBackend.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_group_that_is_not_pinned_keeps_the_fallback_to_the_source_when_the_library_has_not_started()
    {
        var node = NewNode(pinWhen: _ => false, pinStore: _store, lazyLibraryAsync: _ => throw new InvalidOperationException("no library"));
        node.Core.Pins!.EvaluatePins();
        await node.Core.Pins.RefreshAsync(Ct);

        var value = await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("made")), default, Ct);

        value.Should().Be(new Item("made"));
        node.SourceBackend.Writes.Should().Be(1);
    }

    [Fact]
    public async Task The_pin_is_acknowledged_only_after_the_calls_that_started_on_the_source_have_ended()
    {
        var node = NewNode(useLibrary: _ => false, pinWhen: _ => _pinned, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);
        var release = new TaskCompletionSource();
        node.SourceBackend.AfterRead = () => release.Task;
        var inFlight = node.Cache.TryGetAsync("k", Ct).AsTask();
        await UntilAsync(() => node.SourceBackend.Reads == 1);

        _pinned = true;
        pins.EvaluatePins();

        Tokens(pins).Should().NotContain("pin=g", "a call that started on the source is still running");
        node.Core.PinFor("g")!.Applied.Should().BeTrue();
        node.SourceBackend.AfterRead = null;
        (await node.Cache.TryGetAsync("other", Ct)).Should().BeNull();
        node.LibraryBackend.Reads.Should().Be(1, "a call that starts after the pin applied takes the library");
        node.SourceBackend.Reads.Should().Be(1);

        release.SetResult();
        await inFlight;
        pins.EvaluatePins();

        Tokens(pins).Should().Contain("pin=g");
    }

    [Fact]
    public async Task A_library_call_that_falls_back_to_the_source_holds_the_acknowledgement_back_until_it_ends()
    {
        var node = NewNode(useLibrary: _ => true, pinWhen: _ => _pinned, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);
        node.LibraryBackend.ReadOverride = ReadOutcome.Unavailable;
        var release = new TaskCompletionSource();
        node.SourceBackend.AfterRead = () => release.Task;
        var inFlight = node.Cache.TryGetAsync("k", Ct).AsTask();
        await UntilAsync(() => node.SourceBackend.Reads == 1);

        _pinned = true;
        pins.EvaluatePins();
        Tokens(pins).Should().NotContain("pin=g");
        release.SetResult();
        await inFlight;
        pins.EvaluatePins();

        Tokens(pins).Should().Contain("pin=g");
    }

    [Fact]
    public async Task Withdrawing_a_pin_that_was_never_acknowledged_returns_the_group_to_its_predicate_at_once()
    {
        var node = NewNode(useLibrary: _ => false, pinWhen: _ => _pinned, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);
        var running = node.Core.PinFor("g")!;
        running.TryBeginSource().Should().BeTrue();
        _pinned = true;
        pins.EvaluatePins();
        Tokens(pins).Should().NotContain("pin=g", "a call that started on the source is still running");

        _pinned = false;
        pins.EvaluatePins();
        await node.Cache.TryGetAsync("k", Ct);

        node.SourceBackend.Reads.Should().Be(1, "no node was ever shown an acknowledgement, so no switch can be recorded on it");
        running.EndSource();
    }

    [Fact]
    public async Task Withdrawing_an_acknowledged_pin_withdraws_it_at_once_and_fails_unavailable_until_a_read_shows_no_switch_was_recorded()
    {
        var node = NewNode(useLibrary: _ => false, pinWhen: _ => _pinned, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);
        _pinned = true;
        pins.EvaluatePins();
        Tokens(pins).Should().Contain("pin=g");

        _pinned = false;
        pins.EvaluatePins();

        Tokens(pins).Should().NotContain("pin=g");
        var read = async () => await node.Cache.TryGetAsync("k", Ct);
        await read.Should().ThrowAsync<CacheUnavailableException>();
        node.SourceBackend.Reads.Should().Be(0);

        await pins.RefreshAsync(Ct);
        await node.Cache.TryGetAsync("k", Ct);

        node.SourceBackend.Reads.Should().Be(1, "the durable state was read after the withdrawal and holds no switch");
        _store.AckOf("g", "n1#router").Should().EndWith(":2", "the withdrawal reached the store with the read that confirmed it");
    }

    [Fact]
    public async Task A_peer_that_records_the_switch_while_a_node_withdraws_its_pin_keeps_that_node_off_the_source()
    {
        var node = NewNode(useLibrary: _ => false, pinWhen: _ => _pinned, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);
        _pinned = true;
        pins.EvaluatePins();

        _pinned = false;
        pins.EvaluatePins();
        _store.Set("g", SwitchRead.Switched);
        var before = async () => await node.Cache.TryGetAsync("k", Ct);
        await before.Should().ThrowAsync<CacheUnavailableException>();
        await pins.RefreshAsync(Ct);
        (await node.Cache.TryGetAsync("k", Ct)).Should().BeNull();

        node.SourceBackend.Reads.Should().Be(0);
        node.LibraryBackend.Reads.Should().Be(1, "the group is switched now, and a switched group takes the library");
        node.Core.PinFor("g")!.Applied.Should().BeTrue();
    }

    [Fact]
    public async Task A_read_that_began_before_the_withdrawal_does_not_confirm_it()
    {
        var node = NewNode(useLibrary: _ => false, pinWhen: _ => _pinned, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);
        _pinned = true;
        pins.EvaluatePins();
        _store.DuringStateRead = () =>
        {
            _store.DuringStateRead = null;
            _pinned = false;
            pins.EvaluatePins();
        };

        await pins.RefreshAsync(Ct);

        var read = async () => await node.Cache.TryGetAsync("k", Ct);
        await read.Should().ThrowAsync<CacheUnavailableException>("the read may have run before a peer recorded the switch");
        await pins.RefreshAsync(Ct);
        await node.Cache.TryGetAsync("k", Ct);
        node.SourceBackend.Reads.Should().Be(1);
    }

    [Fact]
    public async Task A_library_read_that_outlives_the_state_does_not_fall_back_to_the_source()
    {
        var node = NewNode(useLibrary: _ => true, pinWhen: _ => false, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);
        node.LibraryBackend.ReadOverride = ReadOutcome.Unavailable;
        var release = new TaskCompletionSource();
        node.LibraryBackend.AfterRead = () => release.Task;
        _store.Down = true;
        var inFlight = node.Cache.TryGetAsync("k", Ct).AsTask();
        await UntilAsync(() => node.LibraryBackend.Reads == 1);

        Clock.Advance(pins.StateValidity + TimeSpan.FromSeconds(1));
        release.SetResult();

        var result = async () => await inFlight;
        await result.Should().ThrowAsync<CacheUnavailableException>();
        node.SourceBackend.Reads.Should().Be(0, "the state read that let the call in is too old to let it take the source now");
    }

    [Fact]
    public async Task A_group_switched_while_a_library_read_is_running_does_not_fall_back_to_the_source()
    {
        var node = NewNode(useLibrary: _ => true, pinWhen: _ => false, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);
        node.LibraryBackend.ReadOverride = ReadOutcome.Unavailable;
        var release = new TaskCompletionSource();
        node.LibraryBackend.AfterRead = () => release.Task;
        var inFlight = node.Cache.TryGetAsync("k", Ct).AsTask();
        await UntilAsync(() => node.LibraryBackend.Reads == 1);

        _store.Set("g", SwitchRead.Switched);
        await pins.RefreshAsync(Ct);
        release.SetResult();

        var result = async () => await inFlight;
        await result.Should().ThrowAsync<CacheUnavailableException>();
        node.SourceBackend.Reads.Should().Be(0);
    }

    [Fact]
    public async Task A_library_write_that_outlives_the_state_does_not_fall_back_to_the_source()
    {
        var node = NewNode(useLibrary: _ => true, pinWhen: _ => false, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);
        node.LibraryBackend.WriteOutcome = WriteOutcome.NotSent;
        var release = new TaskCompletionSource();
        node.LibraryBackend.InFlight = _ => release.Task;
        _store.Down = true;
        var inFlight = node.Cache.SetAsync("k", new Item("v"), default, Ct).AsTask();
        await UntilAsync(() => node.LibraryBackend.Writes == 1);

        Clock.Advance(pins.StateValidity + TimeSpan.FromSeconds(1));
        release.SetResult();

        (await inFlight).Should().Be(WriteOutcome.NotSent);
        node.SourceBackend.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_library_call_that_falls_back_while_the_state_is_still_good_takes_the_source()
    {
        var node = NewNode(useLibrary: _ => true, pinWhen: _ => false, pinStore: _store);
        await Settled(node);
        node.LibraryBackend.ReadOverride = ReadOutcome.Unavailable;
        node.LibraryBackend.WriteOutcome = WriteOutcome.NotSent;

        await node.Cache.TryGetAsync("k", Ct);
        await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        (node.SourceBackend.Reads, node.SourceBackend.Writes).Should().Be((1, 1));
    }

    [Fact]
    public async Task A_write_made_after_a_factory_that_outlived_the_state_is_not_sent_to_the_source()
    {
        var node = NewNode(useLibrary: _ => false, pinWhen: _ => false, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);
        var release = new TaskCompletionSource();
        var running = node.Cache.GetOrAddAsync("k", async _ => { await release.Task; return new Item("made"); }, default, Ct).AsTask();
        await UntilAsync(() => node.SourceBackend.Reads == 1);

        _store.Down = true;
        Clock.Advance(pins.StateValidity + TimeSpan.FromSeconds(1));
        release.SetResult();

        (await running).Should().Be(new Item("made"), "the caller still gets the value its factory made");
        node.SourceBackend.Writes.Should().Be(0, "the state that let the call in is too old to tell whether the group was switched meanwhile");
        node.LibraryBackend.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_write_made_after_a_factory_that_outlived_a_fallback_to_the_source_is_not_sent_once_the_state_expired()
    {
        var node = NewNode(useLibrary: _ => true, pinWhen: _ => false, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);
        node.LibraryBackend.ReadOverride = ReadOutcome.Unavailable;
        var release = new TaskCompletionSource();
        var running = node.Cache.GetOrAddAsync("k", async _ => { await release.Task; return new Item("made"); }, default, Ct).AsTask();
        await UntilAsync(() => node.SourceBackend.Reads == 1);

        _store.Down = true;
        Clock.Advance(pins.StateValidity + TimeSpan.FromSeconds(1));
        release.SetResult();

        await running;
        node.SourceBackend.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_write_made_after_a_factory_that_outlived_a_pin_goes_to_the_library_and_not_the_source()
    {
        var node = NewNode(useLibrary: _ => false, pinWhen: _ => _pinned, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);
        var release = new TaskCompletionSource();
        var running = node.Cache.GetOrAddAsync("k", async _ => { await release.Task; return new Item("made"); }, default, Ct).AsTask();
        await UntilAsync(() => node.SourceBackend.Reads == 1);

        _pinned = true;
        pins.EvaluatePins();
        release.SetResult();

        await running;
        node.SourceBackend.Writes.Should().Be(0, "the group is pinned now");
        node.LibraryBackend.Writes.Should().Be(1);
    }

    [Fact]
    public async Task A_setting_that_throws_leaves_the_group_as_it_was()
    {
        var failing = false;
        var node = NewNode(pinWhen: _ => failing ? throw new InvalidOperationException("flags down") : true, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);

        failing = true;
        var changed = pins.EvaluatePins();

        changed.Should().BeFalse();
        Tokens(pins).Should().Contain("pin=g", "an unreadable setting does not unpin a group");
    }

    [Fact]
    public async Task A_switched_group_goes_to_the_library_or_fails_when_the_predicate_throws_and_never_to_the_source()
    {
        _store.Set("g", SwitchRead.Switched);
        var node = NewNode(useLibrary: _ => throw new InvalidOperationException("flags down"), pinWhen: _ => false, pinStore: _store);
        await Settled(node);
        Store["k"] = new Item("stored");

        (await node.Cache.TryGetAsync("k", Ct)).Should().Be(new Item("stored"));

        node.LibraryBackend.Reads.Should().Be(1);
        node.SourceBackend.Reads.Should().Be(0);
        node.LibraryBackend.ReadOverride = ReadOutcome.Unavailable;
        var failing = async () => await node.Cache.GetOrAddAsync("missing", _ => Task.FromResult<Item?>(new Item("made")), default, Ct);
        await failing.Should().ThrowAsync<CacheUnavailableException>();
        node.SourceBackend.Reads.Should().Be(0);
    }

    [Fact]
    public async Task A_predicate_that_throws_for_a_group_that_is_not_switched_still_goes_to_the_source()
    {
        var node = NewNode(useLibrary: _ => throw new InvalidOperationException("flags down"), pinWhen: _ => false, pinStore: _store);
        await Settled(node);

        await node.Cache.TryGetAsync("k", Ct);

        node.SourceBackend.Reads.Should().Be(1);
        node.LibraryBackend.Reads.Should().Be(0);
    }

    [Fact]
    public async Task A_switched_group_is_pinned_whatever_the_setting_says_and_advertises_the_switch()
    {
        _store.Set("g", SwitchRead.Switched);
        var node = NewNode(pinWhen: _ => false, pinStore: _store);
        var pins = node.Core.Pins!;

        await Settled(node);

        Tokens(pins).Should().Contain("pin=g").And.Contain("sw=g");
        node.Core.PinFor("g")!.Switched.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_switch_state_fails_the_group_closed_until_a_read_succeeds()
    {
        _store.Down = true;
        var node = NewNode(pinWhen: _ => false, pinStore: _store);
        var pins = node.Core.Pins!;
        pins.EvaluatePins();
        (await pins.RefreshAsync(Ct)).Should().BeFalse();
        var made = 0;

        var read = async () => await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("made" + made++)), default, Ct);
        var write = await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        await read.Should().ThrowAsync<CacheUnavailableException>();
        write.Should().Be(WriteOutcome.NotSent);
        (made, node.SourceBackend.Reads, node.LibraryBackend.Reads, node.SourceBackend.Writes, node.LibraryBackend.Writes).Should().Be((0, 0, 0, 0, 0));

        _store.Down = false;
        await pins.RefreshAsync(Ct);
        (await node.Cache.TryGetAsync("k", Ct)).Should().BeNull();
        node.LibraryBackend.Reads.Should().Be(1);
    }

    [Fact]
    public async Task A_switch_state_that_holds_an_unknown_value_fails_the_group_closed()
    {
        _store.Set("g", SwitchRead.Unreadable);
        var node = NewNode(pinWhen: _ => false, pinStore: _store);
        node.Core.Pins!.EvaluatePins();

        (await node.Core.Pins.RefreshAsync(Ct)).Should().BeFalse();

        var read = async () => await node.Cache.TryGetAsync("k", Ct);
        await read.Should().ThrowAsync<CacheUnavailableException>();
    }

    [Fact]
    public async Task A_known_state_lets_an_unpinned_group_run_through_a_short_outage_and_fails_it_closed_once_the_last_read_is_too_old()
    {
        var node = NewNode(pinWhen: _ => false, pinStore: _store);
        var pins = node.Core.Pins!;
        await Settled(node);
        _store.Down = true;

        Clock.Advance(pins.StateValidity - TimeSpan.FromSeconds(1));
        (await node.Cache.TryGetAsync("k", Ct)).Should().BeNull();
        Clock.Advance(TimeSpan.FromSeconds(2));

        var read = async () => await node.Cache.TryGetAsync("k", Ct);
        await read.Should().ThrowAsync<CacheUnavailableException>();
        _store.Down = false;
        await pins.RefreshAsync(Ct);
        (await node.Cache.TryGetAsync("k", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_pinned_group_does_not_need_the_state_to_be_known()
    {
        _store.Down = true;
        var node = NewNode(pinWhen: _ => true, pinStore: _store);
        node.Core.Pins!.EvaluatePins();
        Store["k"] = new Item("stored");

        (await node.Cache.TryGetAsync("k", Ct)).Should().Be(new Item("stored"));
    }

    [Fact]
    public async Task A_lost_state_key_is_written_again_by_a_node_that_saw_the_switch()
    {
        _store.Set("g", SwitchRead.Switched);
        var node = NewNode(pinWhen: _ => false, pinStore: _store);
        await Settled(node);

        _store.Lose("g");
        await node.Core.Pins!.RefreshAsync(Ct);

        _store.StateOf("g").Should().Be(SwitchRead.Switched);
        node.Core.PinFor("g")!.Durable.Should().BeTrue();
        node.Core.PinFor("g")!.Applied.Should().BeTrue();
    }

    [Fact]
    public async Task A_lost_state_key_is_restored_from_a_node_that_reports_the_group_switched()
    {
        _store.Nodes.Add(Router("peer", "pin=g", "sw=g"));
        var node = NewNode(pinWhen: _ => false, pinStore: _store);
        await Settled(node);
        Store["k"] = new Item("stored");

        (await node.Cache.TryGetAsync("k", Ct)).Should().Be(new Item("stored"));

        node.LibraryBackend.Reads.Should().Be(1, "the restarted node does not use the source for a group a peer reports switched");
        node.SourceBackend.Reads.Should().Be(0);
        _store.StateOf("g").Should().Be(SwitchRead.Switched);
    }

    [Fact]
    public async Task A_missing_key_with_nothing_reporting_a_switch_means_the_group_is_not_switched()
    {
        _store.Nodes.Add(Router("peer", "pin=g"));
        var node = NewNode(useLibrary: _ => false, pinWhen: _ => false, pinStore: _store);
        await Settled(node);

        await node.Cache.TryGetAsync("k", Ct);

        node.SourceBackend.Reads.Should().Be(1);
        _store.StateOf("g").Should().Be(SwitchRead.Unswitched);
    }

    [Fact]
    public async Task With_the_state_missing_and_the_node_list_unreadable_the_group_is_not_known()
    {
        var node = NewNode(pinWhen: _ => false, pinStore: new NodesDownStore(_store));
        node.Core.Pins!.EvaluatePins();

        (await node.Core.Pins.RefreshAsync(Ct)).Should().BeFalse();

        var read = async () => await node.Cache.TryGetAsync("k", Ct);
        await read.Should().ThrowAsync<CacheUnavailableException>();
    }

    [Fact]
    public async Task A_pinned_group_never_touches_the_source_for_a_read_a_write_or_a_remove()
    {
        var node = NewNode(useLibrary: _ => false, pinWhen: _ => true, pinStore: _store);
        await Settled(node);

        await node.Cache.TryGetAsync("a", Ct);
        await node.Cache.SetAsync("a", new Item("v"), default, Ct);
        await node.Cache.RemoveAsync("a", Ct);

        node.SourceBackend.Reads.Should().Be(0);
        node.SourceBackend.Writes.Should().Be(0);
        node.SourceBackend.Removes.Should().Be(0);
    }

    [Theory]
    [InlineData("a+b")]
    [InlineData("a@b")]
    [InlineData("a=b")]
    [InlineData("a{b")]
    [InlineData("a}b")]
    public void A_group_name_that_cannot_be_a_token_cannot_be_pinned(string group)
    {
        var core = new RouterCore(Clock, _ => true, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var pins = new PinCoordinator(core, _ => true, _store, null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(2), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var create = () => pins.GroupFor(group);

        create.Should().Throw<InvalidOperationException>().WithMessage("*" + group + "*");
    }

    [Fact]
    public async Task Admitting_a_call_of_a_group_that_is_not_pinned_allocates_nothing()
    {
        var node = NewNode(pinWhen: _ => false, pinStore: _store);
        await Settled(node);
        var pin = node.Core.PinFor("g")!;
        for (var warm = 0; warm < 1_000; warm++)
        {
            using var admission = node.Core.Admit(pin, "g", out _);
        }

        var best = long.MaxValue;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++)
            {
                using var admission = node.Core.Admit(pin, "g", out _);
            }

            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        best.Should().Be(0);
    }

    internal static NodeEntry Router(string node, params string[] tokens) =>
        new(node + RouterAdvertisement.FieldSuffix, new RouterAdvertisement(node, "b", tokens).Encode(), TimeSpan.Zero);

    internal static List<string> Tokens(PinCoordinator pins)
    {
        var tokens = new List<string>();
        pins.AddTo(tokens);
        return tokens;
    }

    private static async Task Settled(TestNode node)
    {
        node.Core.Pins!.EvaluatePins();
        (await node.Core.Pins.RefreshAsync(Ct)).Should().BeTrue();
    }

    private sealed class NodesDownStore(FakePinStore inner) : IPinStore
    {
        public Task<SwitchRead> ReadStateAsync(string group, CancellationToken cancellationToken) => inner.ReadStateAsync(group, cancellationToken);

        public Task RestoreSwitchAsync(string group, CancellationToken cancellationToken) => inner.RestoreSwitchAsync(group, cancellationToken);

        public Task<AckPublish> PublishAckAsync(string group, string field, string incarnation, long generation, CancellationToken cancellationToken) => inner.PublishAckAsync(group, field, incarnation, generation, cancellationToken);

        public Task<IReadOnlyDictionary<string, string>> ReadAcksAsync(string group, CancellationToken cancellationToken) => inner.ReadAcksAsync(group, cancellationToken);

        public Task<bool> CommitSwitchAsync(string group, IReadOnlyList<KeyValuePair<string, string>> acknowledgements, CancellationToken cancellationToken) => inner.CommitSwitchAsync(group, acknowledgements, cancellationToken);

        public Task<IReadOnlyList<NodeEntry>> ReadNodesAsync(CancellationToken cancellationToken) => throw new StackExchange.Redis.RedisException("down");
    }
}
