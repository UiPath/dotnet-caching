using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

// What a service observes of a routed cache is what it observes of the library's cache: the same arguments are refused, the same results come back, and the same keys are written.
public sealed class CacheContractTests : RouterTestBase
{
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);

    // One delegate for the whole class, so the allocation test measures the cache and not the delegate.
    private static readonly Func<int, CancellationToken, Task<Item?>> NullGenerator = static (_, _) => Task.FromResult<Item?>(null);

    [Fact]
    public async Task Unavailable_operations_return_the_surface_failure_results()
    {
        await using var node = TestNode.Create(Bus, Clock, [], "unavailable", library: UnavailablePathClient<Item>.Instance);
        node.Cache.CacheNullValues = true;
        ICache<Item> cache = node.Cache;
        var item = new Item("value");

        (await cache.TryAddAsync("k", item, Clock.GetUtcNow() + Life, Ct)).Should().BeFalse();
        (await cache.RefreshAsync("k", Ct)).Should().BeFalse();
        (await cache.TimeToLiveAsync("k", Ct)).Should().BeNull();
        (await cache.ExpireTimeAsync("k", Ct)).Should().BeNull();
        (await cache.TryFillAsync("k", item, Life, Ct)).Outcome.Should().Be(FillOutcome.Failed);
        node.Cache.PendingKeyCount.Should().Be(0);
        node.SourceBackend.TryAdds.Should().Be(0);
    }

    [Fact]
    public async Task An_unavailable_storage_command_does_not_escape_as_an_exception()
    {
        var node = NewNode();
        node.Cache.CacheNullValues = true;
        ICache<Item> cache = node.Cache;
        node.LibraryBackend.WriteFails = new CacheUnavailableException("The command did not reach the store.");

        (await cache.SetAsync("k", new Item("value"), Ct)).Should().BeFalse();
        (await cache.RemoveAsync("k", Ct)).Should().BeFalse();
        node.Cache.PendingKeyCount.Should().Be(0);
        (await cache.GetAsync("k", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_failed_presence_check_returns_false_without_reading_the_value()
    {
        var (inner, script) = ScriptedCache.Create();
        script.Answers["ContainsAsync"] = _ => throw new TimeoutException("Lost presence reply");
        var store = new FakeMigrationCache();
        var library = new LibraryPathClient<Item>(store, new FakeConnection(), key => key, Life) { Cache = inner };
        await using var node = TestNode.Create(Bus, Clock, [], "presence", library: library);
        node.Cache.CacheNullValues = true;
        ICache<Item> cache = node.Cache;

        (await cache.ContainsAsync("k", Ct)).Should().BeFalse();
        script.Calls.Should().Equal("ContainsAsync");
        store.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task A_client_without_a_presence_result_checks_the_value_for_hits_and_misses()
    {
        var store = new FakeMigrationCache { Read = (key, _) => Task.FromResult<object?>(key.Name == "hit" ? new Item("stored") : null) };
        var library = new LibraryPathClient<Item>(store, new FakeConnection(), key => key, Life);
        var node = NewNode(library: library);
        node.Cache.CacheNullValues = true;
        ICache<Item> cache = node.Cache;

        (await cache.ContainsAsync("hit", Ct)).Should().BeTrue();
        (await cache.ContainsAsync("missing", Ct)).Should().BeFalse();
        store.Keys.Select(key => key.Name).Should().Equal("hit", "missing");
        node.SourceBackend.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Presence_and_batch_defaults_leave_unsupported_operations_to_the_caller()
    {
        IPathClient<Item> client = UnavailablePathClient<Item>.Instance;

        client.PlainBatch.Should().BeNull();
        (await client.ContainsAsync("k", Ct)).Should().BeNull();
        var add = await client.TryAddAsync("k", null, null, Ct);
        add.Kind.Should().Be(TryAddKind.Failed);
        add.Failure.Outcome.Should().Be(WriteOutcome.NotSent);
    }

    [Fact]
    public async Task A_missing_single_key_generator_is_rejected_before_contacting_the_cache()
    {
        var node = NewNode();
        ICache<Item> cache = node.Cache;
        var error = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await cache.GetOrAddAsync("k", "state", (Func<string, CancellationToken, Task<Item?>>)null!, Ct));

        error.ParamName.Should().Be("plain");
        node.LibraryBackend.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Absolute_and_default_write_overloads_preserve_values_and_deadlines()
    {
        var node = NewNode(defaultExpiry: Life);
        ICache<Item> cache = node.Cache;
        var item = new Item("value");
        var deadline = Clock.GetUtcNow() + Life + Life;
        KeyValuePair<CacheKey, Item?>[] entries = [new("one", item), new("two", item)];

        (await cache.SetAsync(entries, Ct)).Should().BeTrue();
        node.LibraryBackend.LastDeadline.Should().Be(Clock.GetUtcNow() + Life);
        (await cache.SetAsync(entries, deadline, Ct)).Should().BeTrue();
        node.LibraryBackend.LastDeadline.Should().Be(deadline);
        (await cache.GetAsync("one", Ct)).Should().Be(item);
        (await cache.GetAsync("two", Ct)).Should().Be(item);
        (await cache.TryAddAsync("claim", item, deadline, Ct)).Should().BeTrue();
        node.LibraryBackend.LastDeadline.Should().Be(deadline);
        (await cache.GetAsync("claim", Ct)).Should().Be(item);
        var swapped = await cache.SetIfChangedAsync("claim", new Item("replacement"), EqualityComparer<Item>.Default, deadline, Ct);
        swapped.Outcome.Should().Be(ChangeOutcome.Replaced);
        node.LibraryBackend.LastDeadline.Should().Be(deadline);
        (await cache.GetAsync("claim", Ct)).Should().Be(new Item("replacement"));
    }

    [Fact]
    public async Task Single_key_generators_select_the_requested_expiry_and_pass_the_state()
    {
        var node = NewNode();
        ICache<Item> cache = node.Cache;
        var item = new Item("value");
        var deadline = Clock.GetUtcNow() + Life + Life;

        (await cache.GetOrAddAsync("absolute", _ => Task.FromResult<Item?>(item), deadline, Ct)).Should().Be(item);
        node.LibraryBackend.LastDeadline.Should().Be(deadline);
        (await cache.GetOrAddAsync("state-relative", "relative", Generate, Life, Ct)).Should().Be(new Item("relative"));
        node.LibraryBackend.LastDeadline.Should().Be(Clock.GetUtcNow() + Life);
        (await cache.GetOrAddAsync("state-absolute", "absolute", Generate, deadline, Ct)).Should().Be(new Item("absolute"));
        node.LibraryBackend.LastDeadline.Should().Be(deadline);
        (await cache.GetOrAddWithExpirationAsync("state-generated", item, (value, _) => Task.FromResult(new GeneratedValue<Item>(value, deadline)), Ct)).Should().Be(item);
        node.LibraryBackend.LastDeadline.Should().Be(deadline);
        (await cache.GetAsync("state-generated", Ct)).Should().Be(item);

        static Task<Item?> Generate(string text, CancellationToken _) => Task.FromResult<Item?>(new Item(text));
    }

    [Fact]
    public async Task Batch_generators_take_arguments_and_preserve_expiry_and_skip_runs_for_hits()
    {
        var node = NewNode(defaultExpiry: Life);
        ICache<Item> cache = node.Cache;
        var deadline = Clock.GetUtcNow() + Life + Life;
        KeyValuePair<CacheKey, string>[] entries = [new("one", "first"), new("two", "second")];

        (await cache.GetOrAddAsync(entries, "default", Generate, Ct)).Select(kv => kv.Value).Should().Equal(new Item("defaultfirst"), new Item("defaultsecond"));
        node.LibraryBackend.LastDeadline.Should().Be(Clock.GetUtcNow() + Life);
        KeyValuePair<CacheKey, string>[] relative = [new("three", "third")];
        (await cache.GetOrAddAsync(relative, "relative", Generate, Life, Ct)).Should().ContainSingle().Which.Value.Should().Be(new Item("relativethird"));
        node.LibraryBackend.LastDeadline.Should().Be(Clock.GetUtcNow() + Life);

        KeyValuePair<CacheKey, string>[] absolute = [new("four", "fourth")];
        (await cache.GetOrAddAsync(absolute, "absolute", Generate, deadline, Ct)).Should().ContainSingle().Which.Value.Should().Be(new Item("absolutefourth"));
        node.LibraryBackend.LastDeadline.Should().Be(deadline);

        (await cache.GetOrAddAsync(entries, "ignored", Generate, deadline, Ct)).Select(kv => kv.Value).Should().Equal(new Item("defaultfirst"), new Item("defaultsecond"));
        node.LibraryBackend.FillCalls.Should().Be(4);

        (await cache.GetOrAddAsync([new KeyValuePair<CacheKey, string>("five", "fifth")], (states, token) => Generate("relative", states, token), Life, Ct))
            .Should().ContainSingle().Which.Value.Should().Be(new Item("relativefifth"));
        node.LibraryBackend.LastDeadline.Should().Be(Clock.GetUtcNow() + Life);

        static Task<KeyValuePair<string, Item?>[]> Generate(string prefix, string[] states, CancellationToken _) =>
            Task.FromResult(states.Select(state => new KeyValuePair<string, Item?>(state, new Item(prefix + state))).ToArray());
    }

    [Fact]
    public async Task Expiration_is_validated_as_the_library_does_and_the_maximum_means_no_expiry()
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        ICache<Item> cache = node.Cache;
        var item = new Item("v");
        var past = Clock.GetUtcNow().AddMinutes(-1);
        Func<Task>[] refused =
        [
            async () => await cache.SetAsync("k", item, TimeSpan.Zero, Ct),
            async () => await cache.SetAsync("k", item, past, Ct),
            async () => await cache.TryAddAsync("k", item, TimeSpan.FromSeconds(-1), Ct),
            async () => await cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(item), TimeSpan.Zero, Ct),
            async () => await cache.RefreshAsync("k", past, Ct),
            async () => await cache.SetIfChangedAsync("k", item, EqualityComparer<Item>.Default, TimeSpan.Zero, Ct),
            async () => await cache.TryFillAsync("k", item, past, Ct),
            async () => await cache.SetAsync([new KeyValuePair<CacheKey, Item?>("k", item)], TimeSpan.Zero, Ct),
            async () => await cache.GetOrAddAsync([new KeyValuePair<CacheKey, string>("k", "s")], (_, _) => Task.FromResult(Array.Empty<KeyValuePair<string, Item?>>()), past, Ct),
        ];
        foreach (var call in refused)
        {
            await call.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }

        node.LibraryBackend.Writes.Should().Be(0);

        await cache.SetAsync("k", item, DateTimeOffset.MaxValue, Ct);

        node.LibraryBackend.LastDeadline.Should().BeNull("an absolute maximum is no expiry, as for the library");
    }

    [Fact]
    public async Task A_conditional_create_of_null_is_false_and_a_null_write_removes_on_every_overload()
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        node.LibraryBackend.UsesPlainBatch = true;
        ICache<Item> cache = node.Cache;
        await cache.SetAsync("a", new Item("1"), Life, Ct);
        await cache.SetAsync("b", new Item("2"), Life, Ct);

        (await cache.TryAddAsync("n", null, Ct)).Should().BeFalse();
        node.LibraryBackend.TryAdds.Should().Be(0);
        (await cache.SetAsync("a", null, Life, Ct)).Should().BeTrue();
        (await cache.SetAsync([new KeyValuePair<CacheKey, Item?>("b", null), new KeyValuePair<CacheKey, Item?>("c", new Item("3"))], Life, Ct)).Should().BeTrue();

        (await cache.GetAsync("a", Ct)).Should().BeNull();
        (await cache.GetAsync("b", Ct)).Should().BeNull("the batch removes a null too, as the single write does");
        (await cache.GetAsync("c", Ct))!.Name.Should().Be("3");
        node.LibraryBackend.BatchCalls.Should().Equal("set", "remove");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_batch_read_answers_one_pair_per_input_key_duplicates_included(bool plain)
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        node.LibraryBackend.UsesPlainBatch = plain;
        ICache<Item> cache = node.Cache;
        await cache.SetAsync("k", new Item("1"), Life, Ct);

        var read = await cache.GetAsync(["k", "x", "k"], Ct);

        read.Select(r => r.Key.Name).Should().Equal("k", "x", "k");
        read.Select(r => r.Value?.Name).Should().Equal("1", null, "1");
    }

    [Fact]
    public async Task A_batch_write_that_throws_after_it_was_sent_keeps_every_key_pending_off_the_tier_and_announced()
    {
        var node = NewNode(localCap: TimeSpan.FromMinutes(1), pendingWindow: TimeSpan.FromSeconds(30));
        node.LibraryBackend.UsesPlainBatch = true;
        ICache<Item> cache = node.Cache;
        await cache.SetAsync("a", new Item("old"), Life, Ct);
        (await cache.GetAsync("a", Ct))!.Name.Should().Be("old");
        node.IsLocal("a").Should().BeTrue();
        var published = Bus.Published;
        node.LibraryBackend.BatchFails = new InvalidOperationException("the answer never came");

        (await cache.SetAsync([new KeyValuePair<CacheKey, Item?>("a", new Item("1")), new KeyValuePair<CacheKey, Item?>("a", new Item("2")), new KeyValuePair<CacheKey, Item?>("b", new Item("3"))], Life, Ct)).Should().BeFalse();

        node.IsLocal("a").Should().BeFalse("the key may have changed, so the old copy is gone");
        node.Cache.PendingKeyCount.Should().Be(2, "one pending key per distinct key, until the late commit can no longer land");
        Bus.Published.Should().Be(published + 2, "and peers hear of each distinct key");
    }

    [Fact]
    public async Task A_batch_write_cancelled_after_it_was_sent_settles_its_keys_and_then_throws_the_cancellation()
    {
        var node = NewNode(localCap: TimeSpan.FromMinutes(1), pendingWindow: TimeSpan.FromSeconds(30));
        node.LibraryBackend.UsesPlainBatch = true;
        ICache<Item> cache = node.Cache;
        node.LibraryBackend.BatchFails = new OperationCanceledException();

        var write = async () => await cache.RemoveAsync(["a", "b"], Ct);

        await write.Should().ThrowAsync<OperationCanceledException>();
        node.Cache.PendingKeyCount.Should().Be(2);
    }

    [Fact]
    public async Task A_swap_with_no_answer_is_settled_as_an_unknown_write_and_reports_failed()
    {
        var node = NewNode(localCap: TimeSpan.FromMinutes(1), pendingWindow: TimeSpan.FromSeconds(30));
        ICache<Item> cache = node.Cache;
        await cache.SetAsync("k", new Item("1"), Life, Ct);
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;

        var result = await cache.SetIfChangedAsync("k", new Item("2"), EqualityComparer<Item>.Default, Life, Ct);

        result.Outcome.Should().Be(ChangeOutcome.Failed);
        node.Cache.PendingKeyCount.Should().Be(1, "the swap may have applied");
    }

    [Fact]
    public async Task A_route_that_answers_nothing_refuses_the_swap_the_fill_and_the_remaining_life_as_unavailable()
    {
        var client = UnavailablePathClient<Item>.Instance;

        await ((Func<Task>)(async () => await client.SetIfChangedAsync("k", new Item("v"), EqualityComparer<Item>.Default, null, Ct))).Should().ThrowAsync<CacheUnavailableException>();
        await ((Func<Task>)(async () => await client.TryFillAsync("k", new Item("v"), null, Ct))).Should().ThrowAsync<CacheUnavailableException>();
        await ((Func<Task>)(async () => await client.TimeToLiveAsync("k", Ct))).Should().ThrowAsync<CacheUnavailableException>();
        await ((Func<Task>)(async () => await client.ExpireTimeAsync("k", Ct))).Should().ThrowAsync<CacheUnavailableException>();
    }

    [Fact]
    public async Task The_fill_adds_with_no_existing_value_announces_nothing_and_a_lost_one_carries_the_winners_value_and_expiration()
    {
        var node = NewNode(localCap: TimeSpan.FromMinutes(1));
        node.LibraryBackend.ReportedTtl = Life;
        ICache<Item> cache = node.Cache;
        var published = Bus.Published;

        var added = await cache.TryFillAsync("k", new Item("mine"), Life, Ct);
        var lost = await cache.TryFillAsync("k", new Item("other"), Life, Ct);

        added.Outcome.Should().Be(FillOutcome.Added);
        added.Existing.Should().BeNull("Existing is the winner's value, which is for a lost fill");
        lost.Outcome.Should().Be(FillOutcome.Existed);
        lost.Existing!.Name.Should().Be("mine");
        lost.Expiration.Should().Be(Clock.GetUtcNow() + Life);
        Bus.Published.Should().Be(published, "a fill announces nothing, as the library's does");
    }

    [Fact]
    public async Task The_remaining_life_is_the_expiry_only_read_and_a_negative_one_is_zero()
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        ICache<Item> cache = node.Cache;
        await cache.SetAsync("k", new Item("1"), Life, Ct);
        node.LibraryBackend.ReportedTtl = TimeSpan.FromSeconds(-3);
        var reads = node.LibraryBackend.Reads;

        (await cache.TimeToLiveAsync("k", Ct)).Should().Be(TimeSpan.Zero);

        node.LibraryBackend.Reads.Should().Be(reads, "no value was read or decoded for it");
    }

    [Fact]
    public async Task A_get_or_add_that_hits_the_local_tier_allocates_no_closure_for_its_generator_or_its_state()
    {
        var node = NewNode(localCap: TimeSpan.FromMinutes(1));
        ICache<Item> cache = node.Cache;
        await cache.SetAsync("k", new Item("1"), Life, Ct);
        await cache.GetAsync("k", Ct);
        for (var i = 0; i < 2000; i++)
        {
            await cache.GetOrAddAsync("k", 7, NullGenerator, Ct);
        }

        var perCall = long.MaxValue;
        for (var run = 0; run < 7; run++)
        {
            perCall = Math.Min(perCall, BytesPerLocalHit(cache));
        }

        perCall.Should().BeLessThan(64, "a closure and a delegate would add about 90 bytes");
    }

    [Fact]
    public async Task A_refresh_is_a_write_that_evicts_and_announces_and_an_uncertain_one_stays_pending()
    {
        var node = NewNode(localCap: TimeSpan.FromMinutes(1), pendingWindow: TimeSpan.FromSeconds(30));
        ICache<Item> cache = node.Cache;
        await cache.SetAsync("k", new Item("1"), Life, Ct);
        await cache.GetAsync("k", Ct);
        node.IsLocal("k").Should().BeTrue();
        var published = Bus.Published;

        (await cache.RefreshAsync("k", Life, Ct)).Should().BeTrue();
        node.IsLocal("k").Should().BeFalse("a shorter life must not leave a local copy readable past it");
        Bus.Published.Should().Be(published + 1);
        (await cache.RefreshAsync("absent", Life, Ct)).Should().BeFalse();
        Bus.Published.Should().Be(published + 1, "a refresh of nothing changed nothing");

        node.LibraryBackend.RefreshOutcome = WriteResult.UnknownWithin(TimeSpan.FromSeconds(30));
        (await cache.RefreshAsync("k", Life, Ct)).Should().BeFalse();
        node.Cache.PendingKeyCount.Should().Be(1);
    }

    [Fact]
    public async Task A_refresh_with_no_expiry_given_uses_the_registrations_default()
    {
        var node = NewNode(localCap: TimeSpan.Zero, defaultExpiry: TimeSpan.FromHours(2));
        ICache<Item> cache = node.Cache;
        await cache.SetAsync("k", new Item("1"), Life, Ct);

        await cache.RefreshAsync("k", Ct);

        node.LibraryBackend.LastRefresh.Should().Be(Clock.GetUtcNow().AddHours(2));
    }

    [Fact]
    public async Task A_swap_cancelled_in_flight_is_settled_as_unknown_and_the_cancellation_propagates()
    {
        var node = NewNode(localCap: TimeSpan.FromMinutes(1), pendingWindow: TimeSpan.FromSeconds(30));
        ICache<Item> cache = node.Cache;
        node.LibraryBackend.SwapFails = new OperationCanceledException();

        var swap = async () => await cache.SetIfChangedAsync("k", new Item("2"), EqualityComparer<Item>.Default, Life, Ct);

        await swap.Should().ThrowAsync<OperationCanceledException>();
        node.Cache.PendingKeyCount.Should().Be(1);
    }

    [Fact]
    public async Task With_null_caching_on_a_null_is_stored_created_filled_and_reported_present_and_with_it_off_none_of_those_happen()
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        node.LibraryBackend.UsesPlainBatch = true;
        node.LibraryBackend.StoresNull = true;
        node.Cache.CacheNullValues = true;
        ICache<Item> cache = node.Cache;

        (await cache.SetAsync("a", null, Life, Ct)).Should().BeTrue();
        (await cache.ContainsAsync("a", Ct)).Should().BeTrue("a stored null is present");
        (await cache.GetAsync("a", Ct)).Should().BeNull();
        (await cache.TryAddAsync("b", null, Ct)).Should().BeTrue();
        (await cache.TryFillAsync("c", null, Life, Ct)).Outcome.Should().Be(FillOutcome.Added);
        node.LibraryBackend.BatchCalls.Should().Equal("set");

        node.Cache.CacheNullValues = false;
        (await cache.TryAddAsync("d", null, Ct)).Should().BeFalse();
        (await cache.TryFillAsync("e", null, Life, Ct)).Outcome.Should().Be(FillOutcome.Failed);
        (await cache.SetAsync("a", null, Life, Ct)).Should().BeTrue();
        (await cache.ContainsAsync("a", Ct)).Should().BeFalse("without null caching a null removes");
    }

    [Fact]
    public async Task A_create_that_commits_loses_its_reply_and_answers_false_is_settled_so_the_key_leaves_the_tier_and_is_announced()
    {
        var node = NewNode(localCap: TimeSpan.FromMinutes(1), pendingWindow: TimeSpan.FromSeconds(30));
        ICache<Item> cache = node.Cache;
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        node.LibraryBackend.UnknownLands = true;
        var published = Bus.Published;

        (await cache.TryAddAsync("k", new Item("mine"), Life, Ct)).Should().BeFalse("the caller sees false, as with the library");

        node.Cache.PendingKeyCount.Should().Be(1, "the create may have committed, so the key is pending");
        Bus.Published.Should().Be(published + 1, "and peers are told");
        node.IsLocal("k").Should().BeFalse();
    }

    private static long BytesPerLocalHit(ICache<Item> cache)
    {
        const int Calls = 1000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Calls; i++)
        {
            var hit = cache.GetOrAddAsync("k", 7, NullGenerator, Ct);
            if (!hit.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("A hit of the local tier completes at once.");
            }
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / Calls;
    }
}
