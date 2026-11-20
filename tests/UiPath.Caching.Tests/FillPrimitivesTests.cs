using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

public class FillPrimitivesTests(ITestContextAccessor testContextAccessor)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public enum FillStoreFault
    {
        None,
        Fault,
        Unreadable,
        Lapsed,
        LapsedThrow,
    }

    private CancellationToken Ct => testContextAccessor.Current.CancellationToken;

    [Fact]
    public void An_unset_result_is_a_failure()
    {
        default(FillResult<string>).Outcome.Should().Be(FillOutcome.Failed);
        default(ChangeResult<string>).Outcome.Should().Be(ChangeOutcome.Failed);
    }

    [Fact]
    public async Task The_null_cache_reports_a_fill_and_a_miss()
    {
        (await NullCache.Instance.TryFillAsync("k", "v", Ttl, Ct)).Outcome.Should().Be(FillOutcome.Added);
        (await NullCache.Instance.SetIfChangedAsync("k", "v", EqualityComparer<string>.Default, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Filled);
        (await NullCache.Instance.AsMigrationCache().ReadSnapshotAsync<string>("k", null, Ct)).Found.Should().BeFalse();
    }

    [Fact]
    public async Task A_local_only_cache_fills_once_and_reports_the_value_it_holds()
    {
        using var sut = LocalOnly();

        (await sut.TryFillAsync("k", "first", Ttl, Ct)).Should().Be(new FillResult<string>(FillOutcome.Added, null));
        (await sut.TryFillAsync("k", "second", Ttl, Ct)).Should().Be(new FillResult<string>(FillOutcome.Existed, "first"));
        (await sut.GetAsync<string>("k", Ct)).Should().Be("first");
    }

    [Fact]
    public async Task A_local_only_fill_of_a_null_is_a_failure_unless_nulls_are_cached()
    {
        using var sut = LocalOnly();

        (await sut.TryFillAsync<string>("k", null, Ttl, Ct)).Outcome.Should().Be(FillOutcome.Failed);
        (await sut.SetIfChangedAsync<string>("k", null, EqualityComparer<string>.Default, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Failed);
    }

    [Fact]
    public async Task A_local_only_fill_rejects_a_duration_that_is_not_positive()
    {
        using var sut = LocalOnly();

        var act = async () => await sut.TryFillAsync("k", "v", TimeSpan.Zero, Ct);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_local_only_set_if_changed_fills_leaves_an_equal_value_and_replaces_a_different_one()
    {
        using var sut = LocalOnly();
        var comparer = StringComparer.Ordinal;

        (await sut.SetIfChangedAsync("k", "a", comparer, Ttl, Ct)).Should().Be(new ChangeResult<string>(ChangeOutcome.Filled, "a"));
        (await sut.SetIfChangedAsync("k", "a", comparer, Ttl, Ct)).Should().Be(new ChangeResult<string>(ChangeOutcome.Unchanged, "a"));
        (await sut.SetIfChangedAsync("k", "b", comparer, Ttl, Ct)).Should().Be(new ChangeResult<string>(ChangeOutcome.Replaced, "b"));
        (await sut.GetAsync<string>("k", Ct)).Should().Be("b");
    }

    [Fact]
    public async Task An_unchanged_local_value_keeps_its_deadline()
    {
        using var sut = LocalOnly();
        await sut.SetIfChangedAsync("k", "a", StringComparer.Ordinal, TimeSpan.FromSeconds(30), Ct);
        var before = (await sut.AsMigrationCache().ReadSnapshotAsync<string>("k", null, Ct)).Expiration;

        await sut.SetIfChangedAsync("k", "a", StringComparer.Ordinal, TimeSpan.FromHours(1), Ct);

        (await sut.AsMigrationCache().ReadSnapshotAsync<string>("k", null, Ct)).Expiration.Should().Be(before);
    }

    [Fact]
    public async Task A_local_only_snapshot_is_the_local_entry_or_a_miss()
    {
        using var sut = LocalOnly();

        (await sut.AsMigrationCache().ReadSnapshotAsync<string>("k", null, Ct)).Found.Should().BeFalse();
        await sut.SetAsync("k", "v", Ttl, null, Ct);

        var entry = await sut.AsMigrationCache().ReadSnapshotAsync<string>("k", null, Ct);

        entry.Found.Should().BeTrue();
        entry.Value.Should().Be("v");
        entry.Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow + Ttl, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_fill_the_shared_tier_took_is_kept_locally_without_another_read()
    {
        var shared = new FillStore();
        using var sut = Layered(shared);

        (await sut.TryFillAsync("k", "v", Ttl, Ct)).Outcome.Should().Be(FillOutcome.Added);
        var reads = shared.ReadCalls;

        (await sut.GetAsync<string>("k", Ct)).Should().Be("v");
        shared.ReadCalls.Should().Be(reads);
    }

    [Fact]
    public async Task A_fill_that_finds_a_value_reports_it_and_keeps_it_locally()
    {
        var shared = new FillStore();
        using var first = Layered(shared);
        using var second = Layered(shared);
        await first.TryFillAsync("k", "theirs", Ttl, Ct);

        (await second.TryFillAsync("k", "mine", Ttl, Ct)).Should().Be(new FillResult<string>(FillOutcome.Existed, "theirs"));
        var reads = shared.ReadCalls;

        (await second.GetAsync<string>("k", Ct)).Should().Be("theirs");
        shared.ReadCalls.Should().Be(reads);
    }

    [Fact]
    public async Task A_fill_the_shared_tier_could_not_answer_is_a_failure_that_keeps_the_value_locally()
    {
        var shared = Substitute.For<ICache>();
        shared.TryFillAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new FillResult<string?>(FillOutcome.Failed, null));
        using var sut = Layered(shared);

        (await sut.TryFillAsync("k", "v", Ttl, Ct)).Outcome.Should().Be(FillOutcome.Failed);

        (await sut.GetAsync<string>("k", Ct)).Should().Be("v");
        await shared.DidNotReceive().GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_shared_tier_that_throws_is_a_failure_not_an_exception()
    {
        var shared = Substitute.For<ICache>();
        shared.TryFillAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns<FillResult<string?>>(_ => throw new InvalidOperationException("down"));
        shared.SetIfChangedAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<IEqualityComparer<string?>>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns<ChangeResult<string?>>(_ => throw new InvalidOperationException("down"));
        using var sut = Layered(shared);

        (await sut.TryFillAsync("a", "v", Ttl, Ct)).Outcome.Should().Be(FillOutcome.Failed);
        (await sut.SetIfChangedAsync("b", "v", StringComparer.Ordinal, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Failed);
    }

    [Fact]
    public async Task A_cancelled_fill_throws()
    {
        using var sut = Layered(new FillStore());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await sut.TryFillAsync("k", "v", Ttl, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Set_if_changed_fills_leaves_an_equal_value_and_replaces_a_different_one_in_the_shared_tier()
    {
        var shared = new FillStore();
        using var first = Layered(shared);
        using var second = Layered(shared);
        var comparer = StringComparer.Ordinal;

        (await first.SetIfChangedAsync("k", "a", comparer, Ttl, Ct)).Should().Be(new ChangeResult<string>(ChangeOutcome.Filled, "a"));
        (await second.SetIfChangedAsync("k", "a", comparer, Ttl, Ct)).Should().Be(new ChangeResult<string>(ChangeOutcome.Unchanged, "a"));
        (await second.SetIfChangedAsync("k", "b", comparer, Ttl, Ct)).Should().Be(new ChangeResult<string>(ChangeOutcome.Replaced, "b"));
        (await Layered(shared).GetAsync<string>("k", Ct)).Should().Be("b");
    }

    [Fact]
    public async Task A_snapshot_reads_the_shared_tier_and_not_the_local_copy()
    {
        var shared = new FillStore();
        using var first = Layered(shared);
        using var second = Layered(shared);
        await first.SetIfChangedAsync("k", "a", StringComparer.Ordinal, Ttl, Ct);
        (await first.GetAsync<string>("k", Ct)).Should().Be("a");
        await second.SetIfChangedAsync("k", "b", StringComparer.Ordinal, Ttl, Ct);

        var entry = await first.AsMigrationCache().ReadSnapshotAsync<string>("k", null, Ct);

        entry.Value.Should().Be("b", "the snapshot is the shared tier's, and the first node still holds \"a\" locally");
        (await first.GetAsync<string>("k", Ct)).Should().Be("a");
    }

    [Fact]
    public async Task The_typed_cache_forwards_with_its_policy_and_key_mapping()
    {
        var policy = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(11) };
        var inner = Substitute.For<ICache>();
        var keyStrategy = Substitute.For<ICacheKeyStrategy>();
        keyStrategy.GetCacheKey<string>(Arg.Any<CacheKey>()).Returns((CacheKey)"ns:k");
        var sut = new Cache<string>(inner, keyStrategy, policy);
        var absolute = DateTimeOffset.UtcNow.AddMinutes(3);

        await sut.TryFillAsync("k", "v", Ttl, Ct);
        await sut.TryFillAsync("k", "v", absolute, Ct);
        await sut.SetIfChangedAsync("k", "v", StringComparer.Ordinal, Ttl, Ct);
        await sut.SetIfChangedAsync("k", "v", StringComparer.Ordinal, absolute, Ct);

        await inner.Received(1).TryFillAsync<string>("ns:k", "v", Ttl, policy, Arg.Any<CancellationToken>());
        await inner.Received(1).TryFillAsync<string>("ns:k", "v", absolute, policy, Arg.Any<CancellationToken>());
        await inner.Received(1).SetIfChangedAsync<string>("ns:k", "v", StringComparer.Ordinal, Ttl, policy, Arg.Any<CancellationToken>());
        await inner.Received(1).SetIfChangedAsync<string>("ns:k", "v", StringComparer.Ordinal, absolute, policy, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Concurrent_local_fills_of_one_key_report_one_Added()
    {
        var gate = new GatedChangeTokenFactory();
        using var sut = InMemoryMultilayer.Cache(tokens: gate);

        var (first, second) = RunBlockedPair(
            gate,
            () => sut.TryFillAsync("k", "a", Ttl, Ct).AsTask().GetAwaiter().GetResult().Outcome,
            () => sut.TryFillAsync("k", "b", Ttl, Ct).AsTask().GetAwaiter().GetResult().Outcome);

        new[] { first, second }.Should().BeEquivalentTo([FillOutcome.Added, FillOutcome.Existed]);
    }

    [Fact]
    public void Concurrent_local_changes_of_one_absent_key_report_one_Filled()
    {
        var gate = new GatedChangeTokenFactory();
        using var sut = InMemoryMultilayer.Cache(tokens: gate);

        var (first, second) = RunBlockedPair(
            gate,
            () => sut.SetIfChangedAsync("k", "a", StringComparer.Ordinal, Ttl, Ct).AsTask().GetAwaiter().GetResult().Outcome,
            () => sut.SetIfChangedAsync("k", "b", StringComparer.Ordinal, Ttl, Ct).AsTask().GetAwaiter().GetResult().Outcome);

        new[] { first, second }.Should().BeEquivalentTo([ChangeOutcome.Filled, ChangeOutcome.Replaced]);
    }

    [Fact]
    public async Task A_local_fill_that_expires_sooner_than_asked_reports_the_local_deadline()
    {
        using var sut = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalMaxExpiration = TimeSpan.FromMinutes(1) });

        await sut.TryFillAsync("a", "v", TimeSpan.FromHours(1), Ct);
        await sut.SetIfChangedAsync("b", "v", StringComparer.Ordinal, TimeSpan.FromHours(1), Ct);
        await sut.SetIfChangedAsync("b", "w", StringComparer.Ordinal, TimeSpan.FromHours(1), Ct);

        foreach (var key in new[] { "a", "b" })
        {
            (await sut.AsMigrationCache().ReadSnapshotAsync<string>(key, null, Ct)).Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task A_null_is_equal_only_to_a_null_and_the_comparer_is_never_asked_about_one()
    {
        using var sut = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheNullValues = true });
        var comparer = new NonNullComparer();

        (await sut.SetIfChangedAsync<string>("k", null, comparer, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Filled);
        (await sut.SetIfChangedAsync<string>("k", null, comparer, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Unchanged);
        (await sut.SetIfChangedAsync("k", "x", comparer, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Replaced);
        (await sut.SetIfChangedAsync<string>("k", null, comparer, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Replaced);
    }

    [Fact]
    public async Task A_null_found_in_the_shared_tier_never_reaches_the_comparer()
    {
        var shared = new FillStore();
        shared.Seed<string>("k", null);
        var options = new InMemoryRedisCacheOptions { CacheNullValues = true };
        using var sut = Build(shared, options);

        (await sut.SetIfChangedAsync<string>("k", null, new NonNullComparer(), Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Unchanged);
        (await sut.SetIfChangedAsync("k", "x", new NonNullComparer(), Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Replaced);
    }

    [Fact]
    public async Task A_replacement_is_not_kept_after_a_peer_event_that_came_during_the_call()
    {
        var shared = new FillStore();
        using var sut = Layered(shared);
        shared.Seed("k", "base");
        shared.During = () =>
        {
            shared.Seed("k", "theirs");
            PeerChanged(sut);
        };

        (await sut.SetIfChangedAsync("k", "mine", StringComparer.Ordinal, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Replaced);
        shared.During = null;

        (await sut.GetAsync<string>("k", Ct)).Should().Be("theirs", "the stale replacement was not installed over the peer's write");
    }

    [Fact]
    public async Task A_replacement_is_not_kept_over_a_local_write_that_came_during_the_call()
    {
        var shared = new FillStore();
        using var sut = Layered(shared);
        shared.Seed("k", "base");
        shared.During = () =>
        {
            shared.During = null;
            sut.SetAsync("k", "local", Ttl, null, Ct).AsTask().GetAwaiter().GetResult();
        };

        (await sut.SetIfChangedAsync("k", "mine", StringComparer.Ordinal, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Replaced);

        (await sut.GetAsync<string>("k", Ct)).Should().Be("local");
    }

    [Fact]
    public async Task A_found_value_is_not_kept_after_a_peer_event_that_came_during_the_call()
    {
        var shared = new FillStore();
        using var sut = Layered(shared);
        shared.Seed("k", "old");
        shared.During = () =>
        {
            shared.Seed("k", "theirs");
            PeerChanged(sut);
        };

        (await sut.TryFillAsync("k", "mine", Ttl, Ct)).Should().Be(new FillResult<string>(FillOutcome.Existed, "old"));
        shared.During = null;

        (await sut.GetAsync<string>("k", Ct)).Should().Be("theirs");
    }

    [Fact]
    public async Task A_value_that_failed_to_be_stored_is_not_kept_after_an_event_that_came_during_the_call()
    {
        var shared = new FillStore { Fault = FillStoreFault.Fault };
        using var sut = Layered(shared);
        shared.During = () => PeerChanged(sut);

        (await sut.TryFillAsync("a", "mine", Ttl, Ct)).Outcome.Should().Be(FillOutcome.Failed);
        (await sut.SetIfChangedAsync("b", "mine", StringComparer.Ordinal, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Failed);
        shared.During = null;

        (await sut.GetAsync<string>("a", Ct)).Should().BeNull();
        (await sut.GetAsync<string>("b", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_value_that_failed_to_be_stored_is_kept_when_nothing_came_during_the_call()
    {
        var shared = new FillStore { Fault = FillStoreFault.Fault };
        using var sut = Layered(shared);

        (await sut.TryFillAsync("a", "mine", Ttl, Ct)).Outcome.Should().Be(FillOutcome.Failed);
        (await sut.SetIfChangedAsync("b", "mine", StringComparer.Ordinal, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Failed);

        (await sut.GetAsync<string>("a", Ct)).Should().Be("mine");
        (await sut.GetAsync<string>("b", Ct)).Should().Be("mine");
    }

    [Fact]
    public async Task A_fill_that_found_an_unreadable_value_keeps_nothing_locally()
    {
        var shared = new FillStore { Fault = FillStoreFault.Unreadable };
        using var sut = Layered(shared);

        (await sut.TryFillAsync("k", "mine", Ttl, Ct)).Outcome.Should().Be(FillOutcome.Unreadable);

        (await sut.GetAsync<string>("k", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_found_value_with_no_reported_expiry_is_not_kept_locally()
    {
        var shared = new FillStore { ReportNoExpiration = true };
        using var sut = Layered(shared);
        shared.Seed("k", "theirs");

        (await sut.TryFillAsync("k", "mine", Ttl, Ct)).Outcome.Should().Be(FillOutcome.Existed);
        (await sut.SetIfChangedAsync("k", "theirs", StringComparer.Ordinal, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Unchanged);

        shared.Seed("k", "changed");
        (await sut.GetAsync<string>("k", Ct)).Should().Be("changed");
    }

    [Fact]
    public async Task A_found_value_with_an_expiry_is_kept_locally()
    {
        var shared = new FillStore();
        using var sut = Layered(shared);
        shared.Seed("k", "theirs");

        (await sut.TryFillAsync("k", "mine", Ttl, Ct)).Existing.Should().Be("theirs");

        shared.Seed("k", "changed");
        (await sut.GetAsync<string>("k", Ct)).Should().Be("theirs");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_found_value_whose_reported_expiry_has_passed_is_not_written_locally(bool changeCall)
    {
        var shared = new FillStore { HeldFor = TimeSpan.FromSeconds(-1) };
        using var sut = Layered(shared);
        var policy = new CachePolicy { FailSafeEnabled = true };
        shared.Seed("k", "theirs");
        var writes = LocalWrites(sut);

        if (changeCall)
        {
            (await sut.SetIfChangedAsync("k", "theirs", StringComparer.Ordinal, Ttl, policy, Ct)).Outcome.Should().Be(ChangeOutcome.Unchanged);
        }
        else
        {
            (await sut.TryFillAsync("k", "mine", Ttl, policy, Ct)).Outcome.Should().Be(FillOutcome.Existed);
        }

        LocalWrites(sut).Should().Be(writes);
    }

    [Fact]
    public async Task A_comparer_that_throws_reaches_the_caller_of_a_layered_cache_and_nothing_is_written_or_kept()
    {
        var shared = new FillStore();
        using var first = Layered(shared);
        using var second = Layered(shared);
        await first.SetAsync("k", "a", Ttl, null, Ct);
        var boom = new InvalidOperationException("The comparer failed.");

        var act = async () => await second.SetIfChangedAsync("k", "b", new DelegateComparer(() => throw boom), Ttl, Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        shared.Read<string>("k").Should().Be("a");
        (await second.GetAsync<string>("k", Ct)).Should().Be("a");
    }

    [Fact]
    public async Task A_comparer_that_throws_reaches_the_caller_of_a_local_only_cache_and_nothing_is_written()
    {
        using var sut = LocalOnly();
        await sut.SetAsync("k", "a", Ttl, null, Ct);
        var boom = new InvalidOperationException("The comparer failed.");

        var act = async () => await sut.SetIfChangedAsync("k", "b", new DelegateComparer(() => throw boom), Ttl, Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        (await sut.GetAsync<string>("k", Ct)).Should().Be("a");
    }

    [Fact]
    public async Task Two_replacements_that_commit_in_one_order_and_answer_in_the_other_leave_the_later_commit_local()
    {
        var shared = new FillStore();
        using var sut = Layered(shared);
        shared.Seed("k", "base");
        var committed = new Dictionary<string, TaskCompletionSource>
        {
            ["a"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["b"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var release = new Dictionary<string, TaskCompletionSource>
        {
            ["a"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["b"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        shared.ResponseGate = async value =>
        {
            var name = (string)value!;
            committed[name].SetResult();
            await release[name].Task;
        };

        var a = sut.SetIfChangedAsync("k", "a", StringComparer.Ordinal, Ttl, Ct).AsTask();
        await committed["a"].Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var b = sut.SetIfChangedAsync("k", "b", StringComparer.Ordinal, Ttl, Ct).AsTask();
        await committed["b"].Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        release["b"].SetResult();
        (await b).Outcome.Should().Be(ChangeOutcome.Replaced);
        release["a"].SetResult();
        (await a).Outcome.Should().Be(ChangeOutcome.Replaced);
        shared.ResponseGate = null;

        shared.Read<string>("k").Should().Be("b", "the store holds the later commit");
        (await sut.GetAsync<string>("k", Ct)).Should().Be("b", "the earlier commit answered last and must not be kept over it");
    }

    [Fact]
    public async Task A_read_hit_that_began_before_a_replacement_is_not_kept_after_it()
    {
        var shared = new FillStore();
        using var sut = Layered(shared);
        shared.Seed("k", "old");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shared.ReadGate = async () =>
        {
            reading.SetResult();
            await hold.Task;
        };

        var read = sut.GetAsync<string>("k", Ct).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        shared.ReadGate = null;
        (await sut.SetIfChangedAsync("k", "new", StringComparer.Ordinal, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Replaced);
        hold.SetResult();
        (await read).Should().Be("old");

        (await sut.GetAsync<string>("k", Ct)).Should().Be("new", "the read of the older value was not kept over the replacement");
    }

    [Fact]
    public async Task A_local_change_whose_comparer_outlasts_the_deadline_fails_without_a_write()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var sut = InMemoryMultilayer.Cache(clock: clock);
        await sut.SetAsync("k", "a", TimeSpan.FromMinutes(30), null, Ct);
        var slow = new DelegateComparer(() =>
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            return false;
        });

        var result = await sut.SetIfChangedAsync("k", "b", slow, TimeSpan.FromMinutes(1), Ct);

        result.Outcome.Should().Be(ChangeOutcome.Failed);
        (await sut.GetAsync<string>("k", Ct)).Should().Be("a");
    }

    [Fact]
    public async Task A_local_change_whose_comparer_outlasts_the_local_cap_fails_without_a_write()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var sut = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalMaxExpiration = TimeSpan.FromMinutes(1) }, clock: clock);
        await sut.SetAsync("k", "a", TimeSpan.FromMinutes(30), null, Ct);
        var slow = new DelegateComparer(() =>
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            return false;
        });

        var result = await sut.SetIfChangedAsync("k", "b", slow, TimeSpan.FromMinutes(30), Ct);

        result.Outcome.Should().Be(ChangeOutcome.Failed);
        (await sut.GetAsync<string>("k", Ct)).Should().BeNull("the old entry passed its local cap and nothing was written");
    }

    [Fact]
    public async Task A_local_change_keeps_the_deadline_capped_from_the_start_of_the_call()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var sut = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalMaxExpiration = TimeSpan.FromMinutes(1) }, clock: clock);
        await sut.SetAsync("k", "a", TimeSpan.FromHours(1), null, Ct);
        var start = clock.GetUtcNow();
        var slow = new DelegateComparer(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            return false;
        });

        (await sut.SetIfChangedAsync("k", "b", slow, TimeSpan.FromHours(1), Ct)).Outcome.Should().Be(ChangeOutcome.Replaced);

        (await sut.AsMigrationCache().ReadSnapshotAsync<string>("k", null, Ct)).Expiration.Should().Be(start + TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_local_fill_through_a_custom_entry_factory_reports_the_capped_deadline(bool change)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var sut = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalMaxExpiration = TimeSpan.FromMinutes(1), EntryFactory = new TestCacheEntryFactory() }, clock: clock);

        if (change)
        {
            await sut.SetIfChangedAsync("k", "v", StringComparer.Ordinal, TimeSpan.FromHours(1), Ct);
        }
        else
        {
            await sut.TryFillAsync("k", "v", TimeSpan.FromHours(1), Ct);
        }

        (await sut.AsMigrationCache().ReadSnapshotAsync<string>("k", null, Ct)).Expiration.Should().Be(clock.GetUtcNow() + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_local_comparer_never_runs_while_the_key_is_locked()
    {
        using var sut = InMemoryMultilayer.Cache();
        await sut.SetAsync("k", "base", Ttl, null, Ct);
        var calls = 0;
        var comparer = new DelegateComparer(() =>
        {
            var n = Interlocked.Increment(ref calls);
            Task.Run(() => sut.SetAsync("k", $"racer{n}", Ttl, null, Ct).AsTask(), Ct).Wait(TimeSpan.FromSeconds(5), Ct).Should().BeTrue("a write of the key from another thread is not blocked by the comparer");
            return false;
        });

        var result = await sut.SetIfChangedAsync("k", "mine", comparer, Ttl, Ct);

        result.Should().Be(new ChangeResult<string>(ChangeOutcome.Replaced, "mine"));
        calls.Should().Be(3);
        (await sut.GetAsync<string>("k", Ct)).Should().Be("mine");
    }

    [Theory]
    [InlineData(FillStoreFault.Lapsed)]
    [InlineData(FillStoreFault.LapsedThrow)]
    public async Task A_deadline_that_passed_during_the_call_keeps_nothing_locally(FillStoreFault fault)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var shared = new FillStore { Fault = fault, During = () => clock.Advance(TimeSpan.FromMinutes(2)) };
        using var sut = Layered(shared, clock);
        var policy = new CachePolicy { FailSafeEnabled = true };

        (await sut.TryFillAsync("a", "mine", TimeSpan.FromMinutes(1), policy, Ct)).Outcome.Should().Be(FillOutcome.Failed);
        shared.During = () => clock.Advance(TimeSpan.FromMinutes(2));
        (await sut.SetIfChangedAsync("b", "mine", StringComparer.Ordinal, TimeSpan.FromMinutes(1), policy, Ct)).Outcome.Should().Be(ChangeOutcome.Failed);
        shared.During = null;

        (await sut.GetAsync<string>("a", Ct)).Should().BeNull();
        (await sut.GetAsync<string>("b", Ct)).Should().BeNull();
    }
    private static (T First, T Second) RunBlockedPair<T>(GatedChangeTokenFactory gate, Func<T> first, Func<T> second)
    {
        T? firstResult = default;
        T? secondResult = default;
        var a = new Thread(() => firstResult = first());
        using var started = new ManualResetEventSlim();
        var b = new Thread(() =>
        {
            started.Set();
            secondResult = second();
        });
        a.Start();
        gate.WaitUntilEntered(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue("the first write is inside the local tier");
        b.Start();
        started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        SpinWait.SpinUntil(() => (b.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)).Should().BeTrue();
        gate.Release();
        a.Join();
        b.Join();
        return (firstResult!, secondResult!);
    }

    private static long LocalWrites(MultilayerCache cache) =>
        ((LocalMemorySetter)typeof(MultilayerCache).GetField("_localMemorySetter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!).Writes;
    private static void PeerChanged(MultilayerCache cache) =>
        ((BackgroundWrites)typeof(MultilayerCacheBase).GetField("_background", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!).TouchAll();

    private static MultilayerCache LocalOnly() => Build(NullCache.Instance, new InMemoryCacheOptions());

    private static MultilayerCache Layered(ICache shared, TimeProvider? clock = null)
    {
        var options = new InMemoryRedisCacheOptions();
        return Build(shared, options, clock);
    }

    private static MultilayerCache Build<TOptions>(ICache inner, TOptions options, TimeProvider? clock = null)
        where TOptions : IMultilayerCacheOptions, IMemoryCacheOptions =>
        new(
            KnownCacheProviderNames.InMemory,
            inner,
            new MemoryCacheFactory(TimeProvider.System, NullLoggerFactory.Instance),
            NullChangeTokenFactory.Instance,
            NullTopicFactory.Instance,
            NullCacheEventFactory.Instance,
            NullTelemetryProvider.Instance,
            options,
            options,
            new CacheOptions { AppShortName = "test" },
            distributedLock: NullDistributedLock.Instance,
            policyFactory: NullCachePolicyFactory.Instance,
            clock: clock ?? TimeProvider.System,
            logger: NullLogger.Instance);

    private sealed class DelegateComparer(Func<bool> equal) : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => equal();

        public int GetHashCode(string obj) => obj.GetHashCode(StringComparison.Ordinal);
    }

    private sealed class NonNullComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => string.Equals(x ?? throw new InvalidOperationException("The comparer was asked about a null."), y ?? throw new InvalidOperationException("The comparer was asked about a null."), StringComparison.Ordinal);

        public int GetHashCode(string obj) => obj.GetHashCode(StringComparison.Ordinal);
    }

    private sealed class FillStore : DictionaryCache, ICache, IMigrationCache
    {
        /// <summary>Runs after the store acted and before it answers.</summary>
        public Action? During { get; set; }

        public FillStoreFault Fault { get; set; }

        public bool ReportNoExpiration { get; set; }

        /// <summary>How long from now a found entry reports it has left.</summary>
        public TimeSpan HeldFor { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>Holds each change's answer after the store committed it.</summary>
        public Func<object?, Task>? ResponseGate { get; set; }

        /// <summary>Holds each single read after it read.</summary>
        public Func<Task>? ReadGate { get; set; }

        public ValueTask<ICacheEntry<T?>> ReadSnapshotAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) =>
            GetCacheEntryAsync<T>(cacheKey, policy, token);

        public new async ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
        {
            var entry = await base.GetCacheEntryAsync<T>(cacheKey, policy, token);
            if (ReadGate is { } gate)
            {
                await gate();
            }

            return entry;
        }

        public new ValueTask<FillResult<T>> TryFillAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        {
            if (Fault != FillStoreFault.None)
            {
                During?.Invoke();
                if (Fault == FillStoreFault.LapsedThrow)
                {
                    throw new ArgumentOutOfRangeException(nameof(expiration));
                }

                return new(Fault == FillStoreFault.Unreadable ? new FillResult<T>(FillOutcome.Unreadable, default) : default);
            }

            FillResult<T> answer;
            if (Contains(cacheKey))
            {
                var held = Held<T>(cacheKey);
                answer = new FillResult<T>(FillOutcome.Existed, held.Value, ReportNoExpiration ? null : held.Expiration);
            }
            else
            {
                Seed(cacheKey, value);
                answer = new FillResult<T>(FillOutcome.Added, default, expiration);
            }

            During?.Invoke();
            return new(answer);
        }

        public new async ValueTask<ChangeResult<T>> SetIfChangedAsync<T>(CacheKey cacheKey, T? value, IEqualityComparer<T> comparer, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        {
            if (Fault != FillStoreFault.None)
            {
                During?.Invoke();
                if (Fault == FillStoreFault.LapsedThrow)
                {
                    throw new ArgumentOutOfRangeException(nameof(expiration));
                }

                return default;
            }

            ChangeResult<T> answer;
            if (!Contains(cacheKey))
            {
                Seed(cacheKey, value);
                answer = new ChangeResult<T>(ChangeOutcome.Filled, value, expiration);
            }
            else
            {
                var held = Held<T>(cacheKey);
                if (ChangeComparison.AreEqual(comparer, held.Value, value))
                {
                    answer = new ChangeResult<T>(ChangeOutcome.Unchanged, held.Value, ReportNoExpiration ? null : held.Expiration);
                }
                else
                {
                    Seed(cacheKey, value);
                    answer = new ChangeResult<T>(ChangeOutcome.Replaced, value, expiration);
                }
            }

            During?.Invoke();
            if (ResponseGate is { } gate)
            {
                await gate(value);
            }

            return answer;
        }

        private TestCacheEntry<T?> Held<T>(CacheKey cacheKey) =>
            new() { Value = Read<T>(cacheKey), Expiration = DateTimeOffset.UtcNow + HeldFor, Found = true };
    }
}
