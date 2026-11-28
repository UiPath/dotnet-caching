using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

// The IHashCache<T> contract of a routed hash cache over a scripted source and library, in each mode.
public sealed class HashCacheContractTests : RouterTestBase
{
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);

    public static TheoryData<string> Modes => ["library", "source", "bypass"];

    internal Dictionary<string, StoredHash> Hashes { get; } = [];

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_miss_reads_as_an_empty_hash_and_as_no_item_and_no_metadata(string mode)
    {
        var (node, _, _) = Node(mode);

        (await node.Surface.GetAsync("none", Ct)).Should().BeEmpty();
        (await node.Surface.GetItemAsync("none", "f", Ct)).Should().BeNull();
        (await node.Surface.GetMetadataAsync("none", Ct)).Should().BeNull();
        (await node.Surface.ContainsAsync("none", Ct)).Should().BeFalse();
        var entry = await node.Surface.GetCacheEntryAsync("none", Ct);
        entry.Found.Should().BeFalse();
        entry.Value.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_field_subset_returns_only_the_fields_that_are_found_and_asks_the_client_for_them(string mode)
    {
        var (node, target, _) = Node(mode);
        Seed("k", ("a", "1"), ("b", "2"), ("c", "3"));

        var subset = await node.Surface.GetAsync("k", ["a", "c", "missing"], Ct);

        subset.Keys.Should().BeEquivalentTo("a", "c", "missing");
        subset["missing"].Should().BeNull("a field asked for that is not there is answered as null, as the native read does");
        target.LastFields.Should().Equal("a", "c", "missing");
        (await node.Surface.GetItemAsync("k", "b", Ct))!.Name.Should().Be("2");
        target.LastFields.Should().Equal("b");
        (await node.Surface.GetAsync("k", Ct)).Should().HaveCount(3);
        target.LastFields.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task An_entry_carries_the_expiry_and_the_metadata_the_path_knows_and_the_maximum_date_for_no_expiry(string mode)
    {
        var (node, _, _) = Node(mode);
        var deadline = Clock.GetUtcNow().AddMinutes(7);
        Seed("with", ("a", "1"));
        Hashes["with"].Expiration = deadline;
        Hashes["with"].Metadata = new Dictionary<string, string?> { ["m"] = "x" };
        Seed("without", ("a", "1"));

        var entry = await node.Surface.GetCacheEntryAsync("with", Ct);
        var plain = await node.Surface.GetCacheEntryAsync("without", Ct);

        entry.Found.Should().BeTrue();
        entry.Value.Should().ContainKey("a");
        entry.Expiration.Should().Be(mode == "library" ? deadline : DateTimeOffset.MaxValue, "the source reports no expiry");
        entry.Metadata.Should().BeEquivalentTo(mode == "library" ? new Dictionary<string, string?> { ["m"] = "x" } : null);
        plain.Expiration.Should().Be(DateTimeOffset.MaxValue);
        (await node.Surface.GetMetadataAsync("with", Ct)).Should().BeEquivalentTo(mode == "library" ? new Dictionary<string, string?> { ["m"] = "x" } : null);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_replace_and_a_merge_reach_the_client_with_their_own_mode_and_deadline(string mode)
    {
        var (node, target, other) = Node(mode);
        IDictionary<string, Item?> fields = new Dictionary<string, Item?> { ["a"] = new Item("1") };

        (await node.Surface.SetAsync("k", fields, Life, Ct)).Should().BeTrue();
        target.LastOptions.Should().Be(new HashWriteOptions(HashCacheSetOption.KeyReplace, Clock.GetUtcNow() + Life));
        (await node.Surface.SetAsync("k", fields, new HashCacheEntryOptions(SetOption: HashCacheSetOption.HashReplace), Ct)).Should().BeTrue();
        target.LastOptions.Should().Be(new HashWriteOptions(HashCacheSetOption.HashReplace, mode == "library" ? null : DateTimeOffset.MaxValue));
        (await node.Surface.SetAsync("k", fields, new HashCacheEntryOptions(TimeToLive: Life, SetOption: HashCacheSetOption.HashReplace), Ct)).Should().BeTrue();
        target.LastOptions.Should().Be(new HashWriteOptions(HashCacheSetOption.HashReplace, Clock.GetUtcNow() + Life));
        (await node.Surface.SetAsync("k", fields, Clock.GetUtcNow().AddHours(1), Ct)).Should().BeTrue();
        target.LastOptions.GetValueOrDefault().Deadline.Should().Be(Clock.GetUtcNow().AddHours(1));
        target.Writes.Should().Be(4);
        other.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_merge_keeps_the_fields_the_hash_holds_and_a_replace_drops_them()
    {
        var (node, _, _) = Node("library");
        Seed("k", ("a", "1"), ("b", "2"));

        await node.Surface.SetAsync("k", new Dictionary<string, Item?> { ["c"] = new Item("3") }, new HashCacheEntryOptions(SetOption: HashCacheSetOption.HashReplace), Ct);
        (await node.Surface.GetAsync("k", Ct)).Keys.Should().BeEquivalentTo("a", "b", "c");

        await node.Surface.SetAsync("k", new Dictionary<string, Item?> { ["d"] = new Item("4") }, Ct);
        (await node.Surface.GetAsync("k", Ct)).Keys.Should().BeEquivalentTo("d");
    }

    [Fact]
    public async Task A_write_with_metadata_works_on_the_library_and_is_refused_on_the_source_and_in_bypass()
    {
        var metadata = new Dictionary<string, string?> { ["m"] = "x" };
        IDictionary<string, Item?> fields = new Dictionary<string, Item?> { ["a"] = new Item("1") };
        var (library, libraryTarget, _) = Node("library");
        (await library.Surface.SetAsync("k", fields, new HashCacheEntryOptions(Metadata: metadata), Ct)).Should().BeTrue();
        libraryTarget.LastOptions.GetValueOrDefault().Metadata.Should().BeSameAs(metadata);

        foreach (var mode in new[] { "source", "bypass" })
        {
            var (node, target, _) = Node(mode);
            var write = async () => await node.Surface.SetAsync("m", fields, new HashCacheEntryOptions(Metadata: metadata), Ct);

            await write.Should().ThrowAsync<NotSupportedException>().WithMessage($"*{CacheName}*", mode);
            target.Writes.Should().Be(0, mode);
        }
    }

    [Fact]
    public async Task A_write_with_metadata_that_the_library_did_not_send_never_falls_back_to_the_source()
    {
        var (node, target, source) = Node("library");
        target.WriteOutcome = WriteOutcome.NotSent;
        IDictionary<string, Item?> fields = new Dictionary<string, Item?> { ["a"] = new Item("1") };

        (await node.Surface.SetAsync("k", fields, new HashCacheEntryOptions(Metadata: new Dictionary<string, string?> { ["m"] = "x" }), Ct)).Should().BeFalse();
        source.Writes.Should().Be(0, "the source cannot keep the metadata, so a fallback would drop it");

        (await node.Surface.SetAsync("k", fields, Ct)).Should().BeTrue("a write with no metadata is the source's when the library did not send it, as any write is");
        source.Writes.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_write_of_unknown_outcome_is_false_and_leaves_the_key_pending(string mode)
    {
        var (node, target, _) = Node(mode);
        target.WriteOutcome = WriteOutcome.Unknown;
        IDictionary<string, Item?> fields = new Dictionary<string, Item?> { ["a"] = new Item("1") };

        (await node.Surface.SetAsync("k", fields, Ct)).Should().BeFalse();
        (await node.Surface.RemoveAsync("r", Ct)).Should().BeFalse();

        node.Cache.PendingCount.Should().Be(2);
        target.WriteOutcome = WriteOutcome.Committed;
        (await node.Surface.SetAsync("k", fields, Ct)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Field_names_and_expirations_are_validated_before_any_client_is_asked(string mode)
    {
        var (node, target, other) = Node(mode);
        IDictionary<string, Item?> good = new Dictionary<string, Item?> { ["a"] = new Item("1") };
        var calls = new Func<Task>[]
        {
            async () => await node.Surface.SetAsync("k", new Dictionary<string, Item?> { [" "] = new Item("1") }, Ct),
            async () => await node.Surface.SetAsync("k", new Dictionary<string, Item?> { ["_metadata_"] = new Item("1") }, Ct),
            async () => await node.Surface.GetItemAsync("k", "", Ct),
            async () => await node.Surface.GetAsync("k", ["a", "_version_"], Ct),
            async () => await node.Surface.RemoveFieldsAsync("k", ["_metadata_"], Ct),
            async () => await node.Surface.SetAsync("k", good, TimeSpan.Zero, Ct),
            async () => await node.Surface.SetAsync("k", good, Clock.GetUtcNow().AddMinutes(-1), Ct),
            async () => await node.Surface.GetOrAddAsync("k", _ => Task.FromResult<IDictionary<string, Item?>>(good), -Life, Ct),
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<ArgumentException>();
        }

        (target.Reads + target.Writes + target.FieldRemoves + other.Reads + other.Writes).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task The_maximum_date_means_no_expiry(string mode)
    {
        var (node, target, _) = Node(mode);

        await node.Surface.SetAsync("k", new Dictionary<string, Item?> { ["a"] = new Item("1") }, DateTimeOffset.MaxValue, Ct);

        target.LastOptions.GetValueOrDefault().Deadline.Should().Be(mode == "library" ? null : DateTimeOffset.MaxValue);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task RemoveFieldsAsync_reaches_the_field_form_and_the_many_key_forms_run_one_call_per_key(string mode)
    {
        var (node, target, _) = Node(mode);
        Seed("a", ("f", "1"), ("g", "2"));
        Seed("b", ("f", "1"));
        Seed("c", ("f", "1"));

        (await node.Surface.RemoveFieldsAsync("a", ["f"], Ct)).Should().BeTrue();
        target.LastRemovedFields.Should().Equal("f");
        target.Removes.Should().Be(0);
        Hashes["a"].Fields.Keys.Should().Equal("g");

        (await node.Surface.RemoveManyAsync(["b", "c"], Ct)).Should().BeEmpty();
        target.Removes.Should().Be(2);
        var sets = await node.Surface.SetManyAsync([new KeyValuePair<CacheKey, IDictionary<string, Item?>>("d", new Dictionary<string, Item?> { ["f"] = new Item("x") }), new KeyValuePair<CacheKey, IDictionary<string, Item?>>("e", new Dictionary<string, Item?> { ["f"] = new Item("y") })], Ct);
        sets.Should().BeEmpty();
        target.Writes.Should().Be(2);
        Hashes.Keys.Should().BeEquivalentTo("a", "d", "e");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Refresh_time_to_live_expire_time_and_set_metadata_work_on_the_library_and_are_refused_on_the_source(string mode)
    {
        var (node, target, other) = Node(mode);
        Seed("k", ("a", "1"));
        Hashes["k"].Expiration = Clock.GetUtcNow().AddMinutes(3);
        var metadata = new Dictionary<string, string?> { ["m"] = "x" };

        if (mode == "library")
        {
            (await node.Surface.RefreshAsync("k", Life, Ct)).Should().BeTrue();
            (await node.Surface.ExpireTimeAsync("k", Ct)).Should().Be(Clock.GetUtcNow() + Life);
            (await node.Surface.TimeToLiveAsync("k", Ct)).Should().Be(Life);
            (await node.Surface.RefreshAsync("k", Clock.GetUtcNow().AddHours(1), Ct)).Should().BeTrue();
            (await node.Surface.RefreshAsync("k", Ct)).Should().BeTrue("a refresh with no expiry given renews with the default");
            (await node.Surface.RefreshAsync("missing", Life, Ct)).Should().BeFalse();
            (await node.Surface.TimeToLiveAsync("missing", Ct)).Should().BeNull();
            target.Refreshes.Should().Be(4);
        }
        else
        {
            var refused = new Func<Task>[]
            {
                async () => await node.Surface.RefreshAsync("k", Ct),
                async () => await node.Surface.RefreshAsync("k", Life, Ct),
                async () => await node.Surface.RefreshAsync("k", Clock.GetUtcNow().AddHours(1), Ct),
                async () => await node.Surface.RefreshAsync("k", new HashCacheEntryOptions(TimeToLive: Life), Ct),
                async () => await node.Surface.TimeToLiveAsync("k", Ct),
                async () => await node.Surface.ExpireTimeAsync("k", Ct),
            };
            foreach (var call in refused)
            {
                await call.Should().ThrowAsync<NotSupportedException>();
            }
        }

        var setMetadata = async () => await node.Surface.SetMetadataAsync("k", metadata, Ct);
        await setMetadata.Should().ThrowAsync<NotSupportedException>().WithMessage($"*{CacheName}*", "a metadata-only write is refused on every path");
        other.Refreshes.Should().Be(0);
    }

    [Fact]
    public async Task A_refresh_with_options_replaces_the_metadata_and_a_null_metadata_clears_it_while_the_plain_refresh_leaves_it()
    {
        var (node, target, _) = Node("library");
        Seed("k", ("a", "1"));
        Hashes["k"].Metadata = new Dictionary<string, string?> { ["old"] = "1" };

        (await node.Surface.RefreshAsync("k", Life, Ct)).Should().BeTrue();
        target.LastRefreshHadMetadataForm.Should().BeFalse();
        Hashes["k"].Metadata.Should().ContainKey("old");

        var replacement = new Dictionary<string, string?> { ["new"] = "2" };
        (await node.Surface.RefreshAsync("k", new HashCacheEntryOptions(TimeToLive: Life, Metadata: replacement), Ct)).Should().BeTrue();
        target.LastRefreshHadMetadataForm.Should().BeTrue();
        target.LastRefreshMetadata.Should().BeSameAs(replacement);

        (await node.Surface.RefreshAsync("k", new HashCacheEntryOptions(TimeToLive: Life), Ct)).Should().BeTrue();
        target.LastRefreshHadMetadataForm.Should().BeTrue("the options form always speaks for the metadata");
        target.LastRefreshMetadata.Should().BeNull();
        Hashes["k"].Metadata.Should().BeNull();
    }

    [Fact]
    public async Task The_time_to_live_of_a_hash_that_has_just_expired_is_zero_and_never_negative()
    {
        var (node, target, _) = Node("library");
        target.ReportedExpiry = Clock.GetUtcNow().AddSeconds(-30);

        (await node.Surface.TimeToLiveAsync("k", Ct)).Should().Be(TimeSpan.Zero);
        (await node.Surface.ExpireTimeAsync("k", Ct)).Should().Be(Clock.GetUtcNow().AddSeconds(-30));
    }

    [Fact]
    public async Task A_default_expiry_applies_to_a_write_and_a_refresh_that_name_none()
    {
        var node = Own(HashTestNode.Create(Clock, CacheName, defaultExpiry: Life));
        Seed("k", ("a", "1"));

        await node.Surface.SetAsync("k", new Dictionary<string, Item?> { ["a"] = new Item("1") }, Ct);
        await node.Surface.RefreshAsync("k", Ct);

        node.Library.LastOptions.GetValueOrDefault().Deadline.Should().Be(Clock.GetUtcNow() + Life);
        node.Library.LastRefreshExpiry.Should().Be(CacheExpiry.After(Life));
    }

    [Fact]
    public async Task A_failed_library_read_falls_back_to_the_source_and_never_in_bypass_or_while_the_keys_move()
    {
        var (node, library, source) = Node("library");
        Seed("k", ("a", "1"));
        library.ReadOverride = ReadOutcome.Unavailable;

        (await node.Surface.GetAsync("k", Ct)).Should().ContainKey("a");
        source.Reads.Should().Be(1);

        var (moving, movingLibrary, movingSource) = Node("library");
        movingLibrary.ReadOverride = ReadOutcome.Unavailable;
        movingLibrary.Moving = true;
        (await moving.Surface.GetAsync("k", Ct)).Should().BeEmpty("a client that moves keys has no other client");
        movingSource.Reads.Should().Be(0);

        var (bypass, bypassSource, _) = Node("bypass");
        bypassSource.ReadOverride = ReadOutcome.Unavailable;
        (await bypass.Surface.GetAsync("k", Ct)).Should().BeEmpty();
        bypass.Library.Reads.Should().Be(0);
    }

    [Fact]
    public async Task A_write_that_was_not_sent_by_the_library_is_sent_to_the_source_unless_the_keys_move()
    {
        var (node, library, source) = Node("library");
        library.WriteOutcome = WriteOutcome.NotSent;
        IDictionary<string, Item?> fields = new Dictionary<string, Item?> { ["a"] = new Item("1") };

        (await node.Surface.SetAsync("k", fields, Ct)).Should().BeTrue();
        source.Writes.Should().Be(1);

        var (moving, movingLibrary, movingSource) = Node("library");
        movingLibrary.WriteOutcome = WriteOutcome.NotSent;
        movingLibrary.Moving = true;
        (await moving.Surface.SetAsync("k", fields, Ct)).Should().BeFalse();
        movingSource.Writes.Should().Be(0);
    }

    [Fact]
    public async Task ContainsAsync_believes_a_client_that_can_say_and_reads_when_the_client_cannot()
    {
        var (node, library, _) = Node("library");
        Seed("k", ("a", "1"));
        library.ContainsAnswer = false;

        (await node.Surface.ContainsAsync("k", Ct)).Should().BeFalse("the library's presence check says what a read cannot, for a hash cached empty");

        library.ContainsAnswer = true;
        (await node.Surface.ContainsAsync("absent", Ct)).Should().BeTrue();
        library.ReadOverride = ReadOutcome.Unavailable;
        library.ContainsAnswer = null;
        (await node.Surface.ContainsAsync("k", Ct)).Should().BeTrue("a client that cannot say is replaced by the read, which falls back to the source");
    }

    [Theory]
    [InlineData("source")]
    [InlineData("bypass")]
    public async Task ContainsAsync_on_the_source_is_a_read(string mode)
    {
        var (node, source, _) = Node(mode);
        Seed("k", ("a", "1"));

        (await node.Surface.ContainsAsync("k", Ct)).Should().BeTrue();
        (await node.Surface.ContainsAsync("missing", Ct)).Should().BeFalse();
        source.Reads.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_get_or_add_hit_runs_no_generator_and_a_miss_stores_what_the_generator_made(string mode)
    {
        var (node, target, _) = Node(mode);
        Seed("hit", ("a", "1"));
        var runs = 0;
        Task<IDictionary<string, Item?>> Generate(CancellationToken token)
        {
            runs++;
            return Task.FromResult<IDictionary<string, Item?>>(new Dictionary<string, Item?> { ["g"] = new Item("made") });
        }

        (await node.Surface.GetOrAddAsync("hit", Generate, Life, Ct)).Keys.Should().Equal("a");
        runs.Should().Be(0);

        (await node.Surface.GetOrAddAsync("miss", Generate, Life, Ct)).Keys.Should().Equal("g");
        runs.Should().Be(1);
        Hashes["miss"].Fields.Keys.Should().Equal("g");
        target.LastOptions.Should().Be(new HashWriteOptions(HashCacheSetOption.KeyReplace, Clock.GetUtcNow() + Life));
        (await node.Surface.GetOrAddAsync("miss", "state", (_, _) => throw new InvalidOperationException("a hit"), Ct)).Keys.Should().Equal("g");
    }

    [Fact]
    public async Task Concurrent_callers_of_a_get_or_add_share_one_generator_run_and_each_gets_its_own_hash()
    {
        var (node, _, _) = Node("library");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        async Task<IDictionary<string, Item?>> Generate(CancellationToken token)
        {
            Interlocked.Increment(ref runs);
            entered.TrySetResult();
            await release.Task;
            return new Dictionary<string, Item?> { ["g"] = new Item("made") };
        }

        var callers = Enumerable.Range(0, 4).Select(_ => node.Surface.GetOrAddAsync("k", Generate, Ct).AsTask()).ToArray();
        await entered.Task.WaitAsync(Ct);
        runs.Should().Be(1);
        release.SetResult();
        var results = await Task.WhenAll(callers);

        runs.Should().Be(1);
        results.Should().OnlyContain(r => r.ContainsKey("g"));
        results.Distinct().Should().HaveCount(4, "a caller that changes its hash must not change another's");
    }

    [Fact]
    public async Task A_get_or_add_whose_fill_lost_returns_the_hash_that_won_and_not_the_one_it_made()
    {
        var (node, target, _) = Node("library");
        target.AfterRead = () =>
        {
            Seed("k", ("winner", "w"));
            return Task.CompletedTask;
        };

        var result = await node.Surface.GetOrAddAsync("k", _ => Task.FromResult<IDictionary<string, Item?>>(new Dictionary<string, Item?> { ["mine"] = new Item("m") }), Ct);

        result.Keys.Should().Equal("winner");
        Hashes["k"].Fields.Keys.Should().Equal("winner");
        target.Fills.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task An_empty_generator_result_stores_nothing_and_is_returned_empty(string mode)
    {
        var (node, target, other) = Node(mode);

        var result = await node.Surface.GetOrAddAsync("k", _ => Task.FromResult<IDictionary<string, Item?>>(new Dictionary<string, Item?>()), Ct);

        result.Should().BeEmpty();
        Hashes.Should().BeEmpty();
        (target.Writes + target.Fills + other.Writes + other.Fills).Should().Be(0);
    }

    [Fact]
    public async Task A_get_or_add_that_no_client_can_answer_throws_and_runs_no_generator()
    {
        var (node, library, source) = Node("library");
        library.ReadOverride = ReadOutcome.Unavailable;
        source.ReadOverride = ReadOutcome.Unavailable;
        var runs = 0;

        Task<IDictionary<string, Item?>> Generate(CancellationToken token)
        {
            runs++;
            return Task.FromResult<IDictionary<string, Item?>>(new Dictionary<string, Item?> { ["a"] = new Item("1") });
        }

        var call = async () => await node.Surface.GetOrAddAsync("k", Generate, Ct);

        await call.Should().ThrowAsync<CacheUnavailableException>();
        runs.Should().Be(0);
    }

    [Fact]
    public async Task The_router_clears_a_hash_cache_through_the_source_in_every_mode_and_refuses_while_the_keys_move()
    {
        foreach (var mode in new[] { "library", "source", "bypass" })
        {
            var (node, _, _) = Node(mode);
            Seed("k", ("a", "1"));

            await node.Router.ClearAsync(node.CacheName, Ct);

            node.Source.Clears.Should().Be(1, mode);
            (await node.Surface.GetAsync("k", Ct)).Should().BeEmpty(mode);
        }

        var (moving, library, source) = Node("library");
        library.Moving = true;
        var clear = async () => await moving.Router.ClearAsync(moving.CacheName, Ct);
        await clear.Should().ThrowAsync<NotSupportedException>();
        source.Clears.Should().Be(0);
    }

    [Fact]
    public async Task A_pending_key_is_retired_by_a_flush_and_the_router_waits_for_it_to_settle()
    {
        var (node, target, _) = Node("library");
        target.WriteOutcome = WriteOutcome.Unknown;
        await node.Surface.SetAsync("k", new Dictionary<string, Item?> { ["a"] = new Item("1") }, Ct);
        var settled = node.Cache.WhenPendingSettledAsync();

        node.Cache.PendingCount.Should().Be(1);
        settled.IsCompleted.Should().BeFalse();
        await node.Cache.FlushAsync();

        node.Cache.PendingCount.Should().Be(0);
        await settled.WaitAsync(Ct);
    }

    [Fact]
    public async Task A_key_whose_write_has_no_retry_is_pending_for_the_window_and_not_for_a_second_one()
    {
        var time = new SignalingTimeProvider(Clock);
        var (node, target, _) = Node("library", time);
        target.WriteOutcome = WriteOutcome.Unknown;
        var window = TimeSpan.FromSeconds(30);

        // The waiter looks at the key late; the shared no-retry answer is no hold, so the window runs from the write.
        AdvanceOnce(node, TimeSpan.FromSeconds(20));
        await node.Surface.SetAsync("k", new Dictionary<string, Item?> { ["a"] = new Item("1") }, Ct);
        await time.WaitForTimerAsync(window - TimeSpan.FromSeconds(20), Ct);

        Clock.Advance(window - TimeSpan.FromSeconds(20));

        await node.Cache.WhenPendingSettledAsync().WaitAsync(Ct);
        node.Cache.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task A_completed_task_that_is_not_the_shared_no_retry_answer_is_a_hold_all_the_same()
    {
        var time = new SignalingTimeProvider(Clock);
        var (node, target, _) = Node("library", time);
        target.WriteOutcome = WriteOutcome.Unknown;

        // Equal in value to the shared answer and a different task: it stands for a retry that has ended, whose window is still enforced.
        target.Settled = _ => Task.FromResult(0);
        AdvanceOnce(node, TimeSpan.FromSeconds(20));
        await node.Surface.SetAsync("k", new Dictionary<string, Item?> { ["a"] = new Item("1") }, Ct);
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(30), Ct);

        Clock.Advance(TimeSpan.FromSeconds(10));

        node.Cache.PendingCount.Should().Be(1);
    }

    [Fact]
    public async Task Disposing_the_cache_disposes_the_library_client_and_a_stopped_router_refuses_new_calls()
    {
        var (node, library, _) = Node("library");

        await node.StopAsync();

        library.Disposed.Should().BeTrue();
        var call = async () => await node.Surface.GetAsync("k", Ct);
        await call.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task An_empty_selector_reads_the_whole_hash_and_a_missing_hash_answers_each_field_asked_for_as_null(string mode)
    {
        var (node, target, _) = Node(mode);
        Seed("k", ("a", "1"), ("b", "2"));

        (await node.Surface.GetAsync("k", Array.Empty<string>(), Ct)).Keys.Should().BeEquivalentTo("a", "b");
        target.LastFields.Should().BeNull("an empty selector is a read of the whole hash");

        var none = await node.Surface.GetAsync("nothing", ["x", "y"], Ct);
        none.Should().BeEquivalentTo(new Dictionary<string, Item?> { ["x"] = null, ["y"] = null });
        (await node.Surface.GetAsync("nothing", Ct)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_hash_cached_empty_is_the_answer_it_runs_no_generator_and_is_there_as_an_entry(string mode)
    {
        var (node, target, _) = Node(mode);
        var runs = 0;
        Task<IDictionary<string, Item?>> Generate(CancellationToken token)
        {
            runs++;
            return Task.FromResult<IDictionary<string, Item?>>(new Dictionary<string, Item?> { ["g"] = new Item("made") });
        }

        Hashes["marker"] = new StoredHash { CachedEmpty = true, Expiration = Clock.GetUtcNow().AddMinutes(4) };
        var result = await node.Surface.GetOrAddAsync("marker", Generate, Life, Ct);
        var entry = await node.Surface.GetCacheEntryAsync("marker", Ct);

        if (mode == "library")
        {
            result.Should().BeEmpty();
            runs.Should().Be(0);
            entry.Found.Should().BeTrue();
            entry.Value.Should().BeEmpty();
            entry.Expiration.Should().Be(Clock.GetUtcNow().AddMinutes(4));
            (await node.Surface.ContainsAsync("marker", Ct)).Should().BeTrue();
            target.Fills.Should().Be(0);
        }
        else
        {
            // The source has no such marker: what it holds empty is a miss.
            runs.Should().Be(1);
        }
    }

    [Theory]
    [InlineData("library")]
    [InlineData("source")]
    public async Task A_generator_that_was_paused_when_the_cache_was_cleared_stores_nothing_when_it_resumes(string mode)
    {
        var (node, target, other) = Node(mode);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IDictionary<string, Item?>> Generate(CancellationToken token)
        {
            started.TrySetResult();
            await resume.Task;
            return new Dictionary<string, Item?> { ["g"] = new Item("old") };
        }

        var call = node.Surface.GetOrAddAsync("k", Generate, Life, Ct).AsTask();
        await started.Task;
        await node.Router.ClearAsync(node.CacheName, Ct);
        resume.SetResult();
        var result = await call;

        result.Should().ContainKey("g", "the caller still gets what its generator made");
        Hashes.Should().BeEmpty("nothing the cache held before the clear is stored after it");
        (target.Writes + target.Fills + other.Writes + other.Fills).Should().Be(0);

        // The control: a load that begins after the clear fills as usual.
        (await node.Surface.GetOrAddAsync("k", Generate, Life, Ct)).Should().ContainKey("g");
        Hashes.Keys.Should().Equal("k");
    }

    [Theory]
    [InlineData("library")]
    [InlineData("source")]
    public async Task A_local_reset_leaves_a_paused_load_unable_to_store_or_be_joined(string mode)
    {
        var (node, _, _) = Node(mode);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        async Task<IDictionary<string, Item?>> Old(CancellationToken token)
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult();
            await resume.Task;
            return new Dictionary<string, Item?> { ["g"] = new Item("old") };
        }

        Task<IDictionary<string, Item?>> New(CancellationToken token)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult<IDictionary<string, Item?>>(new Dictionary<string, Item?> { ["n"] = new Item("new") });
        }

        var call = node.Surface.GetOrAddAsync("k", Old, Life, Ct).AsTask();
        await started.Task;
        ((IRouterParticipant)node.Cache).ResetLocal();
        var join = node.Surface.GetOrAddAsync("k", New, Life, Ct).AsTask();
        IDictionary<string, Item?> after;
        try
        {
            after = await join.WaitAsync(Ct);
        }
        finally
        {
            resume.TrySetResult();
        }
        await call;

        after.Keys.Should().Equal(["n"], "a caller that starts after the reset does not join the earlier load");
        runs.Should().Be(2);
        Hashes.Keys.Should().Equal("k");
        Hashes["k"].Fields.Keys.Should().Equal(["n"], "the earlier load did not write after the reset");
    }

    [Fact]
    public async Task The_caller_that_ran_a_shared_load_cannot_change_what_the_others_are_given()
    {
        var (node, _, _) = Node("library");
        for (var round = 0; round < 60; round++)
        {
            var key = $"round{round}";
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<IDictionary<string, Item?>> Generate(CancellationToken token)
            {
                started.TrySetResult();
                await release.Task;
                return new Dictionary<string, Item?> { ["g"] = new Item("made") };
            }

            var leader = node.Surface.GetOrAddAsync(key, Generate, Ct).AsTask();
            await started.Task;
            var followers = Enumerable.Range(0, 3).Select(_ => node.Surface.GetOrAddAsync(key, Generate, Ct).AsTask()).ToArray();
            release.SetResult();

            // The leader changes its hash the moment it has it.
            var own = await leader;
            own["mutated"] = new Item("by the leader");
            var others = await Task.WhenAll(followers);

            others.Should().OnlyContain(f => f.Count == 1 && f.ContainsKey("g"), $"round {round}");
        }
    }

    [Fact]
    public async Task A_fill_started_during_clear_stays_fenced_until_its_epoch_is_invalidated()
    {
        var (node, library, source) = Node("library");
        var generated = new TaskCompletionSource<IDictionary<string, Item?>>();
        Task<IDictionary<string, Item?>>? fill = null;
        source.BeforeClear = () =>
        {
            fill = node.Surface.GetOrAddAsync("key", _ => generated.Task, Ct).AsTask();
            library.OnGap = () => generated.SetResult(new Dictionary<string, Item?> { ["field"] = new Item("old") });
        };

        await Task.Run(() => node.Router.ClearAsync(node.CacheName, Ct), Ct);
        (await fill!).Should().ContainKey("field");

        library.Fills.Should().Be(0);
        Hashes.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_fallback_write_uses_the_fallback_clients_retry_task()
    {
        var (node, source, library) = Node("source");
        source.WriteOutcome = WriteOutcome.NotSent;
        library.WriteOutcome = WriteOutcome.Unknown;
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settledKeys = new List<string>();
        library.Settled = key =>
        {
            settledKeys.Add(key);
            return retry.Task;
        };

        (await node.Surface.SetAsync("key", new Dictionary<string, Item?> { ["field"] = new("value") }, Ct)).Should().BeFalse();
        retry.SetResult();

        settledKeys.Should().Equal("key");
    }

    [Fact]
    public async Task A_merge_that_expires_behind_another_write_leaves_the_hash_alone()
    {
        var (node, library, _) = Node("library");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        library.BeforeWrite = () => release.Task;
        var first = node.Surface.SetAsync("key", new Dictionary<string, Item?> { ["kept"] = new("value") }, Ct).AsTask();
        var options = new HashCacheEntryOptions(ExpireTime: Clock.GetUtcNow() + Life, SetOption: HashCacheSetOption.HashReplace);
        var merge = node.Surface.SetAsync("key", new Dictionary<string, Item?> { ["late"] = new("value") }, options, Ct).AsTask();

        Clock.Advance(Life);
        release.SetResult();

        (await first).Should().BeTrue();
        (await merge).Should().BeFalse();
        Hashes["key"].Fields.Keys.Should().Equal("kept");
        library.Writes.Should().Be(1);
    }

    [Fact]
    public async Task A_clear_waiting_for_a_write_refuses_when_the_keys_begin_moving()
    {
        var (node, library, source) = Node("library");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        library.BeforeWrite = () => release.Task;
        var write = node.Surface.SetAsync("key", new Dictionary<string, Item?> { ["field"] = new("value") }, Ct).AsTask();
        var clear = node.Router.ClearAsync(node.CacheName, Ct);

        library.Moving = true;
        release.SetResult();

        (await write).Should().BeTrue();
        await FluentActions.Awaiting(() => clear).Should().ThrowAsync<NotSupportedException>();
        source.Clears.Should().Be(0);
        (await node.Surface.ContainsAsync("key", Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_key_eviction_invalidates_a_load_before_the_client_drops_its_hint()
    {
        var (node, library, _) = Node("library");
        var generated = new TaskCompletionSource<IDictionary<string, Item?>>();
        var fill = node.Surface.GetOrAddAsync("key", _ => generated.Task, Ct).AsTask();
        library.OnKeyChanged = () => generated.TrySetResult(new Dictionary<string, Item?> { ["field"] = new("old") });

        await Task.Run(() => node.Cache.EvictLocal("key"), Ct);
        (await fill).Should().ContainKey("field");

        library.Fills.Should().Be(0);
        Hashes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_library_empty_marker_answers_an_unreadable_source_without_running_a_generator()
    {
        var (node, source, _) = Node("source");
        source.ReadOverride = ReadOutcome.Unreadable;
        var expires = Clock.GetUtcNow() + Life;
        Hashes["marker"] = new StoredHash { CachedEmpty = true, Expiration = expires };

        var entry = await node.Surface.GetCacheEntryAsync("marker", Ct);
        entry.Found.Should().BeTrue();
        entry.Expiration.Should().Be(expires);
        (await node.Surface.GetOrAddAsync("marker", _ => throw new InvalidOperationException("The marker already answered."), Ct)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("source")]
    [InlineData("bypass")]
    public async Task A_selected_read_distinguishes_a_source_timeout_from_a_confirmed_miss(string mode)
    {
        var (node, source, _) = Node(mode);

        (await node.Surface.GetAsync("key", ["field"], Ct)).Should().Contain("field", null);
        source.Failure = new TimeoutException("Read timed out.");

        (await node.Surface.GetAsync("key", ["field"], Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_flight_leader_gets_a_private_copy_of_the_generated_dictionary()
    {
        var (node, _, _) = Node("library");
        IDictionary<string, Item?> generated = new Dictionary<string, Item?> { ["field"] = new("value") };

        var result = await node.Surface.GetOrAddAsync("key", _ => Task.FromResult(generated), Ct);
        result.Clear();

        generated.Should().ContainKey("field");
    }

    [Fact]
    public async Task Closure_overloads_reject_a_null_generator_even_on_a_hit()
    {
        var (node, library, _) = Node("library");
        Seed("key", ("field", "value"));
        Func<CancellationToken, Task<IDictionary<string, Item?>>> generator = null!;
        var calls = new Func<Task>[]
        {
            () => node.Surface.GetOrAddAsync("key", generator, Ct).AsTask(),
            () => node.Surface.GetOrAddAsync("key", generator, Life, Ct).AsTask(),
            () => node.Surface.GetOrAddAsync("key", generator, Clock.GetUtcNow() + Life, Ct).AsTask(),
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<ArgumentNullException>().WithParameterName("generator");
        }

        library.Reads.Should().Be(0);
    }

    [Fact]
    public void The_name_is_the_registration_name()
    {
        var (node, _, _) = Node("library");

        node.Surface.Name.Should().Be(CacheName);
    }

    // The clock moves once, just before the waiter of the first pending key looks at it.
    private void AdvanceOnce(HashTestNode node, TimeSpan by)
    {
        var moved = 0;
        node.Cache.Pending.BeforeWaiterLooks = _ =>
        {
            if (Interlocked.Exchange(ref moved, 1) == 0)
            {
                Clock.Advance(by);
            }

            return Task.CompletedTask;
        };
    }

    private void Seed(string key, params (string Field, string Value)[] fields) =>
        Hashes[key] = new StoredHash { Fields = fields.ToDictionary(f => f.Field, f => (Item?)new Item(f.Value)) };

    private (HashTestNode Node, FakeHashBackend Target, FakeHashBackend Other) Node(string mode, TimeProvider? time = null)
    {
        var node = Own(HashTestNode.Create(Clock, CacheName, useLibrary: _ => mode == "library", bypass: mode == "bypass" ? () => true : null, store: Hashes, time: time));

        // The flag is read at start: without it the first call sees the flag and begins the switch in the background.
        node.Router.Start();
        node.Router.Mode.Should().Be(mode == "bypass" ? RouterMode.BypassLibrary : RouterMode.Library);
        return mode == "library" ? (node, node.Library, node.Source) : (node, node.Source, node.Library);
    }
}
