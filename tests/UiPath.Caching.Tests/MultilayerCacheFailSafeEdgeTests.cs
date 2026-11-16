using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;
using MemoryEntry = Microsoft.Extensions.Caching.Memory.ICacheEntry;

namespace UiPath.Caching.Tests;

/// <summary>Fail-safe where calls meet: a write inside a removal, the token of a late generator, and callers that join a run under another policy.</summary>
public class MultilayerCacheFailSafeEdgeTests : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SoftTimeout = TimeSpan.FromSeconds(2);
    private static readonly CachePolicy FailSafe = new() { FailSafeEnabled = true, FailSafeMaxStaleAge = TimeSpan.FromHours(1), FailSafeCooldown = TimeSpan.FromSeconds(30) };
    private static readonly CachePolicy Soft = CachePolicyMerger.Merge(new CachePolicy { FailSafeTimeout = SoftTimeout }, FailSafe);
    private static readonly CachePolicy Strict = CachePolicyMerger.Merge(new CachePolicy { FailSafeMaxStaleAge = TimeSpan.FromSeconds(1) }, FailSafe);
    private static readonly CachePolicy Locked = CachePolicyMerger.Merge(new CachePolicy { FailSafeCooldown = TimeSpan.Zero, Lock = new LockProfile { DistributedLockEnabled = true } }, FailSafe);
    private static readonly CacheKey Key = new("k");

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly MultilayerCache _cache;
    private int _calls;

    public MultilayerCacheFailSafeEdgeTests() => _cache = Build();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_write_that_lands_inside_a_removal_leaves_no_kept_value_behind()
    {
        var memory = new HookedMemory(_clock);
        using var cache = Build(memory: memory);
        memory.Removing = () =>
        {
            memory.Removing = null;
            // From another thread, as a concurrent writer comes; were the two ordered by a lock, the write would land after the removal instead.
            Task.Run(() => cache.SetAsync(Key, "v", Lifetime, FailSafe, Ct).AsTask(), Ct).Wait(TimeSpan.FromSeconds(2), Ct);
        };

        await cache.RemoveAsync<string>(Key, Ct);

        var live = await cache.GetAsync<string>(Key, FailSafe, Ct);
        (await FallbackAsync(cache)).Should().Be(live, "a kept value never outlives the entry a removal took");
    }

    [Fact]
    public async Task A_set_racing_a_removal_leaves_no_kept_value_behind()
    {
        var outlived = 0;
        for (var round = 0; round < 20000; round++)
        {
            // A cache per round: its first kept value is the one no stripe lock covers.
            using var cache = Build(track: false);
            using var start = new ManualResetEventSlim(false);
            var setter = Task.Run(
                async () =>
                {
                    start.Wait(Ct);
                    await cache.SetAsync(Key, "v", Lifetime, FailSafe, Ct);
                },
                Ct);
            var remover = Task.Run(
                async () =>
                {
                    start.Wait(Ct);
                    await cache.RemoveAsync<string>(Key, Ct);
                },
                Ct);
            start.Set();
            await Task.WhenAll(setter, remover);

            if (await cache.GetAsync<string>(Key, FailSafe, Ct) is null && await FallbackAsync(cache) is not null)
            {
                outlived++;
            }
        }

        outlived.Should().Be(0, "a removed value is never returned");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Until_the_soft_timeout_the_callers_cancellation_reaches_the_generator(bool sharedRun)
    {
        var policy = Policy(sharedRun);
        await WarmAsync(_cache, policy);
        var release = Source();
        using var caller = new CancellationTokenSource();
        CancellationToken seen = default;

        var pending = _cache.GetOrAddAsync(
            Key,
            ct =>
            {
                seen = ct;
                return release.Task.WaitAsync(ct);
            },
            Lifetime,
            policy,
            caller.Token).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        await caller.CancelAsync();

        // Bounded: with the shared run off the call ends only when its generator does.
        var act = () => pending.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await act.Should().ThrowAsync<OperationCanceledException>();
        seen.IsCancellationRequested.Should().BeTrue();
        await CachePeers.Until(() => _cache.RunsInFlight == 0);
        _cache.LateGenerations.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Past_the_soft_timeout_the_callers_cancellation_does_not_reach_the_late_generator(bool sharedRun)
    {
        var policy = Policy(sharedRun);
        await WarmAsync(_cache, policy);
        var release = Source();
        using var caller = new CancellationTokenSource();

        var pending = _cache.GetOrAddAsync(Key, ct => release.Task.WaitAsync(ct), Lifetime, policy, caller.Token).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(SoftTimeout);
        (await pending).Should().Be("v1");

        // The request that got the kept value ends.
        await caller.CancelAsync();
        release.SetResult("late");
        await CachePeers.Until(() => _cache.LateGenerations == 0);

        (await _cache.GetAsync<string>(Key, policy, Ct)).Should().Be("late");
        _telemetry.Events.Should().Contain(e => e.Name == "cache.fail_safe.background" && e.Properties!["outcome"] == "stored");
    }

    [Fact]
    public async Task The_late_generators_token_outlives_the_run_and_is_cancelled_when_the_cache_is_disposed()
    {
        var logger = new CountingLogger();
        var cache = Build(logger);
        await WarmAsync(cache, Soft);
        using var caller = new CancellationTokenSource();
        CancellationToken seen = default;

        var pending = cache.GetOrAddAsync(
            Key,
            ct =>
            {
                seen = ct;
                return new TaskCompletionSource<string?>().Task.WaitAsync(ct);
            },
            Lifetime,
            Soft,
            caller.Token).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(SoftTimeout);
        (await pending).Should().Be("v1");

        var usable = () => seen.WaitHandle;
        usable.Should().NotThrow("the token is the late generator's, not that of the run that ended");
        seen.IsCancellationRequested.Should().BeFalse();
        var warnings = logger.Warnings;

        cache.Dispose();
        await CachePeers.Until(() => cache.LateGenerations == 0);

        seen.IsCancellationRequested.Should().BeTrue();
        logger.Warnings.Should().Be(warnings, "a generator the disposal ended did not fail");
        _telemetry.Events.Should().Contain(e => e.Name == "cache.fail_safe.background" && e.Properties!["outcome"] == "discarded");
    }

    [Fact]
    public async Task A_late_generator_that_ends_while_the_cache_is_disposed_logs_no_failure()
    {
        var logger = new CountingLogger();
        var memory = new HookedMemory(_clock);
        var cache = Build(logger, memory: memory);
        var release = await LateAsync(cache, Soft);
        var warnings = logger.Warnings;

        // The local tier goes after the kept values: the generator ends between the two.
        memory.Disposing = () =>
        {
            release.SetResult("late");
            SpinWait.SpinUntil(() => cache.LateGenerations == 0, TimeSpan.FromSeconds(5));
        };
        cache.Dispose();

        cache.LateGenerations.Should().Be(0);
        logger.Warnings.Should().Be(warnings, "a late generator that ends during dispose is not a failure");
        _telemetry.Events.Should().Contain(e => e.Name == "cache.fail_safe.background" && e.Properties!["outcome"] == "discarded");
    }

    [Fact]
    public async Task A_joined_call_without_fail_safe_can_cancel_its_wait_for_the_late_generator()
    {
        await WarmAsync(_cache, Soft);
        var release = Source();
        var opted = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, Soft, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        using var joiner = new CancellationTokenSource();
        var plain = _cache.GetOrAddAsync(Key, Producing("own"), Lifetime, new CachePolicy(), joiner.Token).AsTask();
        _clock.Advance(SoftTimeout);
        (await opted).Should().Be("v1");

        await joiner.CancelAsync();

        var act = () => plain.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _calls.Should().Be(1, "the call joined the run: only the warm-up ran a counted generator");
        release.SetResult("late");
        await CachePeers.Until(() => _cache.LateGenerations == 0);
    }

    [Fact]
    public async Task A_joined_batch_call_can_cancel_its_wait_for_the_late_generator()
    {
        await WarmAsync(_cache, Soft);
        var release = Source();
        var opted = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, Soft, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        using var joiner = new CancellationTokenSource();
        var batch = _cache.GetOrAddAsync<string, long>([new(Key, 1L)], static (_, _) => Task.FromException<KeyValuePair<long, string?>[]>(new InvalidOperationException("batch down")), Soft, joiner.Token).AsTask();
        _clock.Advance(SoftTimeout);
        (await opted).Should().Be("v1");

        await joiner.CancelAsync();

        var act = () => batch.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await act.Should().ThrowAsync<OperationCanceledException>();
        release.SetResult("late");
        await CachePeers.Until(() => _cache.LateGenerations == 0);
    }

    [Theory]
    [InlineData(60L * TimeSpan.TicksPerDay)]
    [InlineData(long.MaxValue)]
    public async Task A_soft_timeout_longer_than_a_timer_takes_still_falls_back_on_a_failure(long ticks)
    {
        var policy = CachePolicyMerger.Merge(new CachePolicy { FailSafeTimeout = TimeSpan.FromTicks(ticks) }, FailSafe);
        await WarmAsync(_cache, policy);
        var release = Source();

        var pending = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, policy, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers || pending.IsCompleted);
        release.SetException(new InvalidOperationException("down"));

        (await pending).Should().Be("v1");
        _cache.LateGenerations.Should().Be(0);
    }

    [Fact]
    public async Task A_fail_safe_call_that_joined_a_run_started_without_it_falls_back_when_the_run_fails()
    {
        await WarmAsync(_cache, FailSafe);
        var release = Source();
        var plain = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, new CachePolicy(), Ct).AsTask();
        await CachePeers.Until(() => _cache.RunsInFlight == 1);
        var opted = _cache.GetOrAddAsync(Key, Producing("unused"), Lifetime, FailSafe, Ct).AsTask();

        release.SetException(new InvalidOperationException("down"));

        await plain.Invoking(p => p).Should().ThrowAsync<InvalidOperationException>();
        (await opted).Should().Be("v1");
        _telemetry.Events.Should().ContainSingle(e => e.Name == "cache.fail_safe.served").Which.Properties.Should().Contain("reason", "error");
        (await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct)).Should().Be("v1");
        _calls.Should().Be(1, "the cooldown the fallback started answered the next miss, and the joined call ran no generator");
    }

    [Fact]
    public async Task A_fail_safe_call_that_joined_a_failing_run_gets_the_error_past_the_max_stale_age()
    {
        await WarmAsync(_cache, FailSafe);
        _clock.Advance(FailSafe.FailSafeMaxStaleAge!.Value);
        var release = Source();
        var plain = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, new CachePolicy(), Ct).AsTask();
        await CachePeers.Until(() => _cache.RunsInFlight == 1);
        var opted = _cache.GetOrAddAsync(Key, Producing("unused"), Lifetime, FailSafe, Ct).AsTask();

        release.SetException(new InvalidOperationException("down"));

        await plain.Invoking(p => p).Should().ThrowAsync<InvalidOperationException>();
        await opted.Invoking(p => p).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_fail_safe_call_that_cancels_its_wait_for_a_joined_run_is_not_answered_with_the_kept_value()
    {
        await WarmAsync(_cache, FailSafe);
        var release = Source();
        var plain = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, new CachePolicy(), Ct).AsTask();
        await CachePeers.Until(() => _cache.RunsInFlight == 1);
        using var joiner = new CancellationTokenSource();
        var opted = _cache.GetOrAddAsync(Key, Producing("unused"), Lifetime, FailSafe, joiner.Token).AsTask();

        await joiner.CancelAsync();

        await opted.Invoking(p => p).Should().ThrowAsync<OperationCanceledException>();
        _telemetry.Events.Should().NotContain(e => e.Name == "cache.fail_safe.served");
        release.SetResult("v2");
        (await plain).Should().Be("v2");
    }

    [Fact]
    public async Task A_fail_safe_call_that_joins_a_run_after_a_removal_is_not_answered_with_the_removed_value()
    {
        var locks = new HeldLeaseLock();
        using var cache = Build(distributedLock: locks);
        await WarmAsync(cache, FailSafe);

        // The run falls back to the kept value, then waits for its lease to go.
        var starter = cache.GetOrAddAsync(Key, Throwing, Lifetime, Locked, Ct).AsTask();
        await locks.Releasing.Task.WaitAsync(Ct);
        await cache.RemoveAsync<string>(Key, Ct);
        var joiner = cache.GetOrAddAsync(Key, Producing("fresh"), Lifetime, Locked, Ct).AsTask();
        locks.Release.SetResult();

        (await starter).Should().Be("v1", "it fell back before the removal");
        (await joiner).Should().Be("fresh", "the removal completed before this call began");
    }

    [Fact]
    public async Task A_fail_safe_call_that_joined_a_run_is_not_answered_with_a_value_a_write_replaced()
    {
        var locks = new HeldLeaseLock();
        using var cache = Build(distributedLock: locks);
        await WarmAsync(cache, FailSafe);
        var starter = cache.GetOrAddAsync(Key, Throwing, Lifetime, Locked, Ct).AsTask();
        await locks.Releasing.Task.WaitAsync(Ct);
        // No cooldown under this policy, so the call joins the run instead of being answered at once.
        var joiner = cache.GetOrAddAsync(Key, Producing("unused"), Lifetime, Locked, Ct).AsTask();
        joiner.IsCompleted.Should().BeFalse();

        (await cache.SetAsync(Key, "newer", Lifetime, FailSafe, Ct)).Should().BeTrue();
        locks.Release.SetResult();

        (await starter).Should().Be("v1");
        (await joiner).Should().Be("newer", "the value the run fell back to is no longer the one kept");
    }

    [Fact]
    public async Task A_callers_own_max_stale_age_bounds_what_it_is_served()
    {
        // Kept under a one-hour stale age, and now a minute past its local expiry.
        await WarmAsync(_cache, FailSafe);
        var lenient = CachePolicyMerger.Merge(new CachePolicy { FailSafeMaxStaleAge = TimeSpan.FromSeconds(61) }, FailSafe);

        var act = () => _cache.GetOrAddAsync(Key, Throwing, Lifetime, Strict, Ct).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>("the value is older than this caller allows");
        (await _cache.GetOrAddAsync(Key, Throwing, Lifetime, lenient, Ct)).Should().Be("v1", "the value is still kept for a caller that allows its age");
    }

    [Fact]
    public async Task The_cooldown_serves_a_caller_only_within_its_own_max_stale_age()
    {
        await WarmAsync(_cache, FailSafe);
        (await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct)).Should().Be("v1");

        (await _cache.GetOrAddAsync(Key, Producing("fresh"), Lifetime, Strict, Ct)).Should().Be("fresh");
    }

    [Fact]
    public async Task A_fail_safe_call_that_joined_a_run_gets_the_error_when_the_value_is_past_its_own_max_stale_age()
    {
        await WarmAsync(_cache, FailSafe);
        var release = Source();
        var starter = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, FailSafe, Ct).AsTask();
        await CachePeers.Until(() => _cache.RunsInFlight == 1);
        var joiner = _cache.GetOrAddAsync(Key, Producing("unused"), Lifetime, Strict, Ct).AsTask();

        release.SetException(new InvalidOperationException("down"));

        (await starter).Should().Be("v1");
        await joiner.Invoking(j => j).Should().ThrowAsync<InvalidOperationException>("the run's fallback is older than this caller allows");
        _calls.Should().Be(1, "the call joined the run");
    }

    [Fact]
    public async Task A_fail_safe_call_that_joined_a_failing_plain_run_gets_the_error_past_its_own_max_stale_age()
    {
        await WarmAsync(_cache, FailSafe);
        var release = Source();
        var plain = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, new CachePolicy(), Ct).AsTask();
        await CachePeers.Until(() => _cache.RunsInFlight == 1);
        var joiner = _cache.GetOrAddAsync(Key, Producing("unused"), Lifetime, Strict, Ct).AsTask();

        release.SetException(new InvalidOperationException("down"));

        await plain.Invoking(p => p).Should().ThrowAsync<InvalidOperationException>();
        await joiner.Invoking(j => j).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_disposal_as_a_generator_becomes_late_still_waits_for_it_and_cleans_up()
    {
        var cache = Build();
        await WarmAsync(cache, Soft);
        var release = Source();
        CancellationToken seen = default;
        cache.LateStarting = cache.Dispose;

        var pending = cache.GetOrAddAsync(
            Key,
            ct =>
            {
                seen = ct;
                ct.Register(static () => throw new InvalidOperationException("a callback that throws"));
                return release.Task;
            },
            Lifetime,
            Soft,
            Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(SoftTimeout);

        (await pending).Should().Be("v1");
        seen.IsCancellationRequested.Should().BeTrue("the disposal reached the generator");
        cache.LateGenerations.Should().Be(1, "the generator ignores its token and still runs");

        release.SetResult("late");
        await CachePeers.Until(() => cache.LateGenerations == 0);
        var handle = () => seen.WaitHandle;
        handle.Should().Throw<ObjectDisposedException>("the generator's source went with it");
    }

    [Fact]
    public async Task A_joined_call_gets_the_expiration_a_late_generator_names()
    {
        await WarmAsync(_cache, Soft);
        var release = new TaskCompletionSource<GeneratedValue<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opted = _cache.GetOrAddWithExpirationAsync(Key, _ => release.Task, Soft, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        var plain = _cache.GetOrAddResultAsync(Key, Producing("own"), Lifetime, new CachePolicy(), Ct).AsTask();
        _clock.Advance(SoftTimeout);
        (await opted).Should().Be("v1");

        var named = _clock.GetUtcNow() + TimeSpan.FromHours(2);
        release.SetResult(new GeneratedValue<string>("late", named));

        (await plain).Should().Be(new CacheResult<string>("late", named, false));
        await CachePeers.Until(() => _cache.LateGenerations == 0);
    }

    [Fact]
    public async Task A_miss_runs_a_generator_of_its_own_while_one_from_before_a_removal_still_runs()
    {
        var first = await LateAsync(_cache, Soft);
        await _cache.RemoveAsync<string>(Key, Ct);
        (await _cache.SetAsync(Key, "v2", Lifetime, Soft, Ct)).Should().BeTrue();
        _clock.Advance(Lifetime * 2);
        var second = Source();
        var started = 0;

        // The first generator still runs, for a value that was removed: this miss falls back to v2 and needs a generator of its own.
        var opted = _cache.GetOrAddAsync(
            Key,
            _ =>
            {
                Interlocked.Increment(ref started);
                return second.Task;
            },
            Lifetime,
            Soft,
            Ct).AsTask();
        var plain = _cache.GetOrAddAsync(Key, Producing("own"), Lifetime, new CachePolicy(), Ct).AsTask();
        first.SetResult("from-before-the-removal");
        await CachePeers.Until(() => _cache.LateGenerations == 0);
        second.SetResult("v3");

        (await opted).Should().Be("v3");
        (await plain).Should().Be("v3", "it joined the run of the generator that answers for v2");
        started.Should().Be(1);
        (await _cache.GetAsync<string>(Key, Soft, Ct)).Should().Be("v3", "the generator from before the removal stored nothing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_answer_that_is_not_stored_leaves_the_value_a_write_kept_since(bool expired)
    {
        await WarmAsync(_cache, Soft);
        var release = new TaskCompletionSource<GeneratedValue<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = _cache.GetOrAddWithExpirationAsync(Key, _ => release.Task, Soft, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(SoftTimeout);
        (await pending).Should().Be("v1");

        // The write lands after the late generator saw its value current and before its answer is handled.
        _cache.LateStoreStarting = () => _cache.SetAsync(Key, "newer", Lifetime, Soft, Ct).AsTask().GetAwaiter().GetResult();
        release.SetResult(expired ? new GeneratedValue<string>("past", _clock.GetUtcNow() - TimeSpan.FromSeconds(1)) : new GeneratedValue<string>(null));
        await CachePeers.Until(() => _cache.LateGenerations == 0);

        (await _cache.GetAsync<string>(Key, Soft, Ct)).Should().Be("newer");
        _clock.Advance(Lifetime * 2);
        (await _cache.GetOrAddAsync(Key, Throwing, Lifetime, Soft, Ct)).Should().Be("newer", "the late answer was for the value before it");
    }

    [Fact]
    public async Task The_stale_age_counts_from_the_deadline_the_local_tier_gave_the_entry()
    {
        var sizes = new PausingSizes();
        using var cache = Build(options: new InMemoryCacheOptions { SizeLimit = 1000, SizeProvider = sizes });
        var cap = TimeSpan.FromSeconds(100);
        var capped = CachePolicyMerger.Merge(new CachePolicy { LocalExpiration = cap }, FailSafe);

        // The local tier reads the clock for its cap, and ten seconds pass before the value is kept for fail-safe.
        sizes.At(1, () => _clock.Advance(TimeSpan.FromSeconds(10)));
        (await cache.GetOrAddAsync(Key, Producing("v1"), TimeSpan.FromHours(1), capped, Ct)).Should().Be("v1");
        _clock.Advance(TimeSpan.FromSeconds(95));
        (await cache.GetAsync<string>(Key, capped, Ct)).Should().BeNull("the local copy expired five seconds ago");

        var strict = CachePolicyMerger.Merge(new CachePolicy { FailSafeMaxStaleAge = TimeSpan.FromSeconds(1) }, capped);
        var lenient = CachePolicyMerger.Merge(new CachePolicy { FailSafeMaxStaleAge = TimeSpan.FromSeconds(6) }, capped);
        var act = () => cache.GetOrAddAsync(Key, Throwing, TimeSpan.FromHours(1), strict, Ct).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>("the value is five seconds stale");
        (await cache.GetOrAddAsync(Key, Throwing, TimeSpan.FromHours(1), lenient, Ct)).Should().Be("v1");
    }

    [Fact]
    public async Task A_late_result_does_not_replace_a_value_written_while_its_generator_ran()
    {
        await WarmAsync(_cache, Soft);
        var release = Source();
        var pending = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, Soft, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);

        // Before the soft timeout: the value the generator started against goes, and a newer one comes.
        await _cache.RemoveAsync<string>(Key, Ct);
        (await _cache.SetAsync(Key, "v2", Lifetime, Soft, Ct)).Should().BeTrue();
        _clock.Advance(SoftTimeout);

        (await pending).Should().Be("v2", "the value kept at the timeout answers the call");
        release.SetResult("late");
        await CachePeers.Until(() => _cache.LateGenerations == 0);

        (await _cache.GetAsync<string>(Key, Soft, Ct)).Should().Be("v2", "the generator began before v2 was written");
        _telemetry.Events.Should().Contain(e => e.Name == "cache.fail_safe.background" && e.Properties!["outcome"] == "discarded");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_late_generator_that_fails_leaves_no_unobserved_exception(bool joinerLeft)
    {
        const string failure = "the late generator failed with nobody waiting";
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerExceptions.Any(x => x.Message == failure))
            {
                Interlocked.Increment(ref unobserved);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await FailLateAsync(failure, joinerLeft);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Volatile.Read(ref unobserved).Should().Be(0);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task An_older_write_that_keeps_its_value_late_does_not_erase_a_newer_writes_fallback()
    {
        var memory = new HookedMemory(_clock);
        using var cache = Build(memory: memory);
        Task<bool>? newer = null;

        // The first write keeps its value after its local commit: the newer write comes there, from another thread.
        memory.Keeping = () =>
        {
            newer = Task.Run(() => cache.SetAsync(Key, "newer", Lifetime, FailSafe, Ct).AsTask(), Ct);
            newer.Wait(TimeSpan.FromMilliseconds(300), Ct);
        };
        (await cache.SetAsync(Key, "older", Lifetime, FailSafe, Ct)).Should().BeTrue();
        (await newer!).Should().BeTrue();

        var live = await cache.GetAsync<string>(Key, FailSafe, Ct);
        live.Should().NotBeNull();
        _clock.Advance(Lifetime * 2);
        (await FallbackAsync(cache)).Should().Be(live, "the write that holds the local tier holds the fallback too");
    }

    [Fact]
    public async Task A_dropped_kept_value_gives_back_its_size()
    {
        using var cache = Build(options: new InMemoryCacheOptions { SizeLimit = 1, SizeProvider = new PausingSizes() });
        await WarmAsync(cache, Soft);
        var release = Source();
        var pending = cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, Soft, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(SoftTimeout);
        (await pending).Should().Be("v1");

        // A late answer the cache does not store drops the value it replaced.
        release.SetResult(null);
        await CachePeers.Until(() => cache.LateGenerations == 0);

        var other = new CacheKey("other");
        (await cache.GetOrAddAsync(other, Producing("b"), Lifetime, FailSafe, Ct)).Should().Be("b");
        _clock.Advance(Lifetime * 2);
        (await cache.GetOrAddAsync(other, Throwing, Lifetime, FailSafe, Ct)).Should().Be("b", "the size limit has room for it once the dropped value is gone");
    }

    [Fact]
    public async Task A_plain_write_that_committed_before_the_first_fail_safe_write_does_not_erase_its_fallback()
    {
        var memory = new HookedMemory(_clock);
        using var cache = Build(memory: memory);

        // Nothing is kept yet, so the plain write is in no turn: the first write under fail-safe runs to its end right after the plain one's commit.
        memory.Committed = () => cache.SetAsync(Key, "newer", Lifetime, FailSafe, Ct).AsTask().GetAwaiter().GetResult();
        (await cache.SetAsync(Key, "plain", Lifetime, new CachePolicy(), Ct)).Should().BeTrue();

        (await cache.GetAsync<string>(Key, FailSafe, Ct)).Should().Be("newer");
        _clock.Advance(Lifetime * 2);
        (await FallbackAsync(cache)).Should().Be("newer", "the plain write was the older of the two");
    }

    [Fact]
    public async Task A_plain_write_that_commits_after_the_first_fail_safe_write_drops_its_fallback()
    {
        var memory = new HookedMemory(_clock);
        using var cache = Build(memory: memory);

        // As above, with the first write under fail-safe done before the plain one commits.
        memory.Creating = () => cache.SetAsync(Key, "older", Lifetime, FailSafe, Ct).AsTask().GetAwaiter().GetResult();
        (await cache.SetAsync(Key, "plain", Lifetime, new CachePolicy(), Ct)).Should().BeTrue();

        (await cache.GetAsync<string>(Key, FailSafe, Ct)).Should().Be("plain");
        _clock.Advance(Lifetime * 2);
        (await FallbackAsync(cache)).Should().BeNull("a value written without fail-safe replaced the kept one");
    }

    [Fact]
    public async Task An_abandoned_generator_that_answers_with_a_past_expiration_leaves_a_newer_runs_fallback()
    {
        // Continuations inline: the abandoned run has ended by the time its generator is completed below.
        var old = new TaskCompletionSource<GeneratedValue<string>>();
        using var caller = new CancellationTokenSource();
        var abandoned = _cache.GetOrAddWithExpirationAsync(Key, _ => old.Task, FailSafe, caller.Token).AsTask();
        await caller.CancelAsync();
        await abandoned.Invoking(a => a).Should().ThrowAsync<OperationCanceledException>();

        (await _cache.GetOrAddAsync(Key, Producing("v2"), Lifetime, FailSafe, Ct)).Should().Be("v2");
        _clock.Advance(Lifetime * 2);
        old.SetResult(new GeneratedValue<string>("past", _clock.GetUtcNow() - TimeSpan.FromSeconds(1)));

        (await FallbackAsync(_cache)).Should().Be("v2", "a generator every caller left answers for nobody");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_answer_that_is_not_stored_leaves_the_value_a_write_kept_while_its_generator_ran(bool expired)
    {
        await WarmAsync(_cache, FailSafe);
        var release = new TaskCompletionSource<GeneratedValue<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = _cache.GetOrAddWithExpirationAsync(Key, _ => release.Task, FailSafe, Ct).AsTask();

        (await _cache.SetAsync(Key, "newer", Lifetime, FailSafe, Ct)).Should().BeTrue();
        release.SetResult(expired ? new GeneratedValue<string>("past", _clock.GetUtcNow() - TimeSpan.FromSeconds(1)) : new GeneratedValue<string>(null));
        await pending;

        (await _cache.GetAsync<string>(Key, FailSafe, Ct)).Should().Be("newer");
        _clock.Advance(Lifetime * 2);
        (await FallbackAsync(_cache)).Should().Be("newer", "the answer was for the value kept before the write");
    }

    [Fact]
    public async Task A_batch_answer_without_the_key_leaves_the_value_a_write_kept_while_its_generator_ran()
    {
        await WarmAsync(_cache, FailSafe);
        var release = new TaskCompletionSource<KeyValuePair<long, string?>[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = _cache.GetOrAddAsync<string, long>([new(Key, 1L)], (_, _) => release.Task, FailSafe, Ct).AsTask();

        (await _cache.SetAsync(Key, "newer", Lifetime, FailSafe, Ct)).Should().BeTrue();
        release.SetResult([]);
        await batch;

        _clock.Advance(Lifetime * 2);
        (await FallbackAsync(_cache)).Should().Be("newer");
    }

    [Fact]
    public async Task A_batch_answer_without_the_key_drops_the_value_kept_before_it()
    {
        await WarmAsync(_cache, FailSafe);

        await _cache.GetOrAddAsync<string, long>([new(Key, 1L)], static (_, _) => Task.FromResult(Array.Empty<KeyValuePair<long, string?>>()), FailSafe, Ct);

        (await FallbackAsync(_cache)).Should().BeNull("the source answered that the key has no value");
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CachePolicy Policy(bool sharedRun) =>
        sharedRun ? Soft : CachePolicyMerger.Merge(new CachePolicy { Lock = new LockProfile { LocalSingleFlightEnabled = false } }, Soft);

    private static TaskCompletionSource<string?> Source() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Stores "v1" under <paramref name="policy"/> and lets its local copy expire.</summary>
    private async Task WarmAsync(MultilayerCache cache, CachePolicy policy)
    {
        (await cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, policy, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);
    }

    /// <summary>Warms the key, expires it and runs a generator past the soft timeout; the source completes it later.</summary>
    private async Task<TaskCompletionSource<string?>> LateAsync(MultilayerCache cache, CachePolicy policy)
    {
        await WarmAsync(cache, policy);
        var release = Source();
        var pending = cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, policy, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(SoftTimeout);
        (await pending).Should().Be("v1");
        cache.LateGenerations.Should().Be(1);
        return release;
    }

    /// <summary>What a miss with a failing generator is answered with: the kept value, or null when the failure reaches the caller.</summary>
    private async Task<string?> FallbackAsync(MultilayerCache cache)
    {
        try
        {
            return await cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A generator goes late and then fails; with <paramref name="joinerLeft"/> a call without fail-safe joined the run and cancelled its wait first.</summary>
    private async Task FailLateAsync(string failure, bool joinerLeft)
    {
        await WarmAsync(_cache, Soft);
        var release = Source();
        var opted = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, Soft, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        using var joiner = new CancellationTokenSource();
        var plain = joinerLeft ? _cache.GetOrAddAsync(Key, Producing("own"), Lifetime, new CachePolicy(), joiner.Token).AsTask() : Task.FromCanceled<string?>(new CancellationToken(true));
        _clock.Advance(SoftTimeout);
        (await opted).Should().Be("v1");
        await joiner.CancelAsync();
        await plain.Invoking(p => p).Should().ThrowAsync<OperationCanceledException>();

        release.SetException(new InvalidOperationException(failure));
        await CachePeers.Until(() => _cache.LateGenerations == 0);
    }

    private Task<string?> Throwing(CancellationToken token)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromException<string?>(new InvalidOperationException("down"));
    }

    private Func<CancellationToken, Task<string?>> Producing(string? value) => _ =>
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(value);
    };

    private MultilayerCache Build(Microsoft.Extensions.Logging.ILogger? logger = null, IDistributedLock? distributedLock = null, IMemoryCacheFactory? memory = null, bool track = true, InMemoryCacheOptions? options = null)
    {
        options ??= new InMemoryCacheOptions();
        var cache = new MultilayerCache(
            KnownCacheProviderNames.InMemory,
            NullCache.Instance,
            memory ?? new MemoryCacheFactory(_clock, NullLoggerFactory.Instance),
            NullChangeTokenFactory.Instance,
            NullTopicFactory.Instance,
            NullCacheEventFactory.Instance,
            _telemetry,
            options,
            options,
            new CacheOptions { AppShortName = "test" },
            distributedLock: distributedLock ?? NullDistributedLock.Instance,
            policyFactory: NullCachePolicyFactory.Instance,
            clock: _clock,
            logger: logger ?? NullLogger.Instance);
        return track ? RunTables.Track(cache) : cache;
    }

    /// <summary>The local tier, with a step a test runs inside a removal or the disposal.</summary>
    private sealed class HookedMemory(TimeProvider clock) : IMemoryCacheFactory, Microsoft.Extensions.Caching.Memory.IMemoryCache
    {
        private readonly MemoryCacheFactory _factory = new(clock, NullLoggerFactory.Instance);
        private Microsoft.Extensions.Caching.Memory.IMemoryCache _inner = null!;

        public Action? Removing { get; set; }

        public Action? Disposing { get; set; }

        /// <summary>Runs once, as the next entry is created, before it is committed.</summary>
        public Action? Creating { get; set; }

        /// <summary>Runs once, right after the next entry is committed.</summary>
        public Action? Committed { get; set; }

        /// <summary>Runs once, as the next fail-safe value is stored.</summary>
        public Action? Keeping { get; set; }

        public Microsoft.Extensions.Caching.Memory.IMemoryCache Get(IMemoryCacheOptions memoryOptions)
        {
            _inner = _factory.Get(memoryOptions);
            return this;
        }

        public MemoryEntry CreateEntry(object key)
        {
            var creating = Creating;
            Creating = null;
            creating?.Invoke();
            if (key is not string && Keeping is { } keeping)
            {
                Keeping = null;
                keeping();
            }

            return new HookedEntry(this, _inner.CreateEntry(key));
        }

        public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);

        public void Remove(object key)
        {
            Removing?.Invoke();
            _inner.Remove(key);
        }

        public void Dispose()
        {
            Disposing?.Invoke();
            _inner.Dispose();
        }

        private sealed class HookedEntry(HookedMemory owner, MemoryEntry inner) : MemoryEntry
        {
            public object Key => inner.Key;

            public object? Value
            {
                get => inner.Value;
                set => inner.Value = value;
            }

            public DateTimeOffset? AbsoluteExpiration
            {
                get => inner.AbsoluteExpiration;
                set => inner.AbsoluteExpiration = value;
            }

            public TimeSpan? AbsoluteExpirationRelativeToNow
            {
                get => inner.AbsoluteExpirationRelativeToNow;
                set => inner.AbsoluteExpirationRelativeToNow = value;
            }

            public TimeSpan? SlidingExpiration
            {
                get => inner.SlidingExpiration;
                set => inner.SlidingExpiration = value;
            }

            public IList<Microsoft.Extensions.Primitives.IChangeToken> ExpirationTokens => inner.ExpirationTokens;

            public IList<Microsoft.Extensions.Caching.Memory.PostEvictionCallbackRegistration> PostEvictionCallbacks => inner.PostEvictionCallbacks;

            public Microsoft.Extensions.Caching.Memory.CacheItemPriority Priority
            {
                get => inner.Priority;
                set => inner.Priority = value;
            }

            public long? Size
            {
                get => inner.Size;
                set => inner.Size = value;
            }

            public void Dispose()
            {
                inner.Dispose();
                var committed = owner.Committed;
                owner.Committed = null;
                committed?.Invoke();
            }
        }
    }

    /// <summary>A distributed lock whose leases are granted at once and go only when the test lets them.</summary>
    private sealed class HeldLeaseLock : IDistributedLock
    {
        public TaskCompletionSource Releasing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IAsyncDisposable> AcquireAsync(string key, TimeSpan expiry, TimeSpan waitTimeout, CancellationToken token) => new(new Lease(this));

        private sealed class Lease(HeldLeaseLock owner) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                owner.Releasing.TrySetResult();
                await owner.Release.Task;
            }
        }
    }

    /// <summary>Sizes every entry as one, and runs a step once, inside the sizing of the given entry counted from when it was set.</summary>
    private sealed class PausingSizes : ICacheEntrySizeProvider
    {
        private Action? _then;
        private int _left;

        public void At(int sizing, Action then)
        {
            _left = sizing;
            _then = then;
        }

        public long GetSize(UiPath.Caching.ICacheEntry entry)
        {
            if (_then is { } then && --_left == 0)
            {
                _then = null;
                then();
            }

            return 1;
        }
    }

    private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger
    {
        private int _warnings;

        public int Warnings => Volatile.Read(ref _warnings);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Interlocked.Increment(ref _warnings);
            }
        }
    }
}
