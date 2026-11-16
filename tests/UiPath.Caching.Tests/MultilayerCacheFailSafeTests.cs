using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests;

/// <summary>Fail-safe on the in-memory multilayer cache: the last value is returned when the generator fails or runs late, within its stale age and cooldown, and never after a removal.</summary>
public class MultilayerCacheFailSafeTests : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);
    private static readonly CachePolicy FailSafe = new() { FailSafeEnabled = true, FailSafeMaxStaleAge = TimeSpan.FromHours(1), FailSafeCooldown = TimeSpan.FromSeconds(30) };
    private static readonly CacheKey Key = new("k");

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly MultilayerCache _cache;
    private int _calls;

    public MultilayerCacheFailSafeTests() => _cache = Build(new InMemoryCacheOptions());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_expired_value_is_returned_when_the_generator_throws()
    {
        await WarmAsync("v1");
        _clock.Advance(Lifetime * 2);

        var value = await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct);

        value.Should().Be("v1");
        _calls.Should().Be(2, "the warm-up ran, then the generator ran and failed");
        _telemetry.Events.Should().ContainSingle(e => e.Name == "cache.fail_safe.served")
            .Which.Properties.Should().Contain("reason", "error").And.Contain("cache.name", KnownCacheProviderNames.InMemory);
    }

    [Fact]
    public async Task The_value_is_not_returned_as_a_hit()
    {
        await WarmAsync("v1");
        _clock.Advance(Lifetime * 2);
        (await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct)).Should().Be("v1");

        (await _cache.GetAsync<string>(Key, FailSafe, Ct)).Should().BeNull("a read is never served a stale value");
        (await _cache.GetCacheEntryAsync<string>(Key, FailSafe, Ct)).Found.Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_kept_value_the_generator_failure_reaches_the_caller()
    {
        var act = async () => await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_policy_without_fail_safe_keeps_throwing()
    {
        (await _cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, (CachePolicy?)null, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);

        var act = async () => await _cache.GetOrAddAsync(Key, Throwing, Lifetime, (CachePolicy?)null, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_value_written_without_fail_safe_is_not_kept()
    {
        await WarmAsync("v1");
        (await _cache.SetAsync(Key, "v2", Lifetime, (CachePolicy?)null, Ct)).Should().BeTrue();
        _clock.Advance(Lifetime * 2);

        var act = async () => await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>("the newer write replaced the kept value and kept nothing");
    }

    [Fact]
    public async Task A_set_under_fail_safe_is_kept()
    {
        (await _cache.SetAsync(Key, "set", Lifetime, FailSafe, Ct)).Should().BeTrue();
        _clock.Advance(Lifetime * 2);

        (await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct)).Should().Be("set");
    }

    [Fact]
    public async Task The_hard_factory_timeout_falls_back_too()
    {
        await WarmAsync("v1");
        _clock.Advance(Lifetime * 2);
        var policy = Merge(new CachePolicy { FactoryTimeout = TimeSpan.FromMilliseconds(20) });

        static async Task<string?> Hanging(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "late";
        }

        var value = await _cache.GetOrAddAsync(Key, Hanging, Lifetime, policy, Ct);

        value.Should().Be("v1");
    }

    [Fact]
    public async Task Past_its_max_stale_age_the_value_is_gone()
    {
        await WarmAsync("v1");
        _clock.Advance(Lifetime + FailSafe.FailSafeMaxStaleAge!.Value + TimeSpan.FromSeconds(1));

        var act = async () => await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task The_max_stale_age_counts_from_the_local_expiry()
    {
        await WarmAsync("v1");
        _clock.Advance(Lifetime + FailSafe.FailSafeMaxStaleAge!.Value - TimeSpan.FromSeconds(1));

        (await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct)).Should().Be("v1");
    }

    [Fact]
    public async Task The_cooldown_returns_the_value_without_running_the_generator_and_ends()
    {
        await WarmAsync("v1");
        _clock.Advance(Lifetime * 2);
        (await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct)).Should().Be("v1");

        _clock.Advance(FailSafe.FailSafeCooldown!.Value - TimeSpan.FromSeconds(1));
        (await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct)).Should().Be("v1");
        _calls.Should().Be(2, "the cooldown skips the generator");

        _clock.Advance(TimeSpan.FromSeconds(2));
        (await _cache.GetOrAddAsync(Key, Producing("v2"), Lifetime, FailSafe, Ct)).Should().Be("v2", "after the cooldown the generator runs again");
        _calls.Should().Be(3);
    }

    [Fact]
    public async Task The_cooldown_never_extends_the_max_stale_age()
    {
        var policy = Merge(new CachePolicy { FailSafeMaxStaleAge = TimeSpan.FromMinutes(1), FailSafeCooldown = TimeSpan.FromMinutes(10) });
        (await _cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, policy, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime + TimeSpan.FromSeconds(30));
        (await _cache.GetOrAddAsync(Key, Throwing, Lifetime, policy, Ct)).Should().Be("v1");

        _clock.Advance(TimeSpan.FromMinutes(1));

        var act = async () => await _cache.GetOrAddAsync(Key, Throwing, Lifetime, policy, Ct);
        await act.Should().ThrowAsync<InvalidOperationException>("the stale age ended inside the cooldown");
    }

    [Fact]
    public async Task A_removed_key_is_never_returned()
    {
        await WarmAsync("v1");
        await _cache.RemoveAsync<string>(Key, Ct);

        var act = async () => await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_key_removed_after_it_expired_is_never_returned()
    {
        await WarmAsync("v1");
        _clock.Advance(Lifetime * 2);
        await _cache.RemoveAsync<string>(Key, Ct);

        var act = async () => await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_removal_during_the_cooldown_ends_it()
    {
        await WarmAsync("v1");
        _clock.Advance(Lifetime * 2);
        (await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct)).Should().Be("v1");
        await _cache.RemoveAsync<string>(Key, Ct);

        var act = async () => await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_removal_while_the_generator_runs_is_not_answered_with_the_older_value()
    {
        await WarmAsync("v1");
        _clock.Advance(Lifetime * 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string?> FailingLate(CancellationToken ct)
        {
            started.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("down");
        }

        var pending = _cache.GetOrAddAsync(Key, FailingLate, Lifetime, FailSafe, Ct).AsTask();
        await started.Task;
        await _cache.RemoveAsync<string>(Key, Ct);
        release.SetResult();

        await pending.Invoking(p => p).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_value_that_skips_fail_safe_is_not_kept_and_drops_the_older_one()
    {
        await WarmAsync("v1");
        _clock.Advance(Lifetime * 2);
        var skipped = await _cache.GetOrAddWithExpirationAsync(Key, static _ => Task.FromResult(new GeneratedValue<string>("not-found") { SkipFailSafe = true }), FailSafe, Ct);
        skipped.Should().Be("not-found");
        _clock.Advance(TimeSpan.FromDays(1));

        var act = async () => await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>("neither the skipped value nor the one before it is kept");
    }

    [Fact]
    public async Task A_generator_that_names_its_expiration_falls_back_too()
    {
        (await _cache.GetOrAddWithExpirationAsync(Key, (Clock: _clock, Lifetime), static (s, _) => Task.FromResult(new GeneratedValue<string>("v1", s.Clock.GetUtcNow() + s.Lifetime)), FailSafe, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);

        var value = await _cache.GetOrAddWithExpirationAsync<string>(Key, static _ => throw new InvalidOperationException("down"), FailSafe, Ct);

        value.Should().Be("v1");
    }

    [Fact]
    public async Task A_null_answer_drops_the_kept_value()
    {
        await WarmAsync("v1");
        _clock.Advance(Lifetime * 2);
        (await _cache.GetOrAddAsync(Key, Producing(null), Lifetime, FailSafe, Ct)).Should().BeNull();

        var act = async () => await _cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>("the source said there is nothing, which is newer than the kept value");
    }

    [Fact]
    public async Task The_callers_cancellation_is_not_answered_with_the_kept_value()
    {
        await WarmAsync("v1");
        _clock.Advance(Lifetime * 2);
        using var caller = new CancellationTokenSource();

        async Task<string?> Cancelling(CancellationToken ct)
        {
            await caller.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "never";
        }

        var act = async () => await _cache.GetOrAddAsync(Key, Cancelling, Lifetime, FailSafe, caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Callers_that_joined_the_failing_run_get_the_kept_value()
    {
        await WarmAsync("v1");
        _clock.Advance(Lifetime * 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        async Task<string?> Failing(CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("down");
        }

        var first = _cache.GetOrAddAsync(Key, Failing, Lifetime, FailSafe, Ct).AsTask();
        await started.Task;
        var joined = _cache.GetOrAddAsync(Key, Failing, Lifetime, FailSafe, Ct).AsTask();
        release.SetResult();

        (await Task.WhenAll(first, joined)).Should().Equal("v1", "v1");
        runs.Should().Be(1, "the second caller joined the first one's run");
        _telemetry.Events.Count(e => e.Name == "cache.fail_safe.served").Should().Be(1, "one activation, however many callers");
    }

    [Fact]
    public async Task The_result_of_a_fallback_is_neither_generated_nor_dated()
    {
        await WarmAsync("v1");
        _clock.Advance(Lifetime * 2);

        var result = await _cache.GetOrAddResultAsync(Key, Throwing, Lifetime, FailSafe, Ct);
        var cooling = await _cache.GetOrAddResultAsync(Key, Throwing, Lifetime, FailSafe, Ct);

        result.Should().Be(new CacheResult<string>("v1", null, false));
        cooling.Should().Be(new CacheResult<string>("v1", null, false));
    }

    [Fact]
    public async Task Past_the_soft_timeout_the_kept_value_is_returned_and_the_late_value_is_stored()
    {
        var policy = Merge(new CachePolicy { FailSafeTimeout = TimeSpan.FromSeconds(2) });
        (await _cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, policy, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, policy, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(TimeSpan.FromSeconds(2));

        (await pending).Should().Be("v1");
        _telemetry.Events.Should().Contain(e => e.Name == "cache.fail_safe.served" && e.Properties!["reason"] == "timeout");
        _cache.LateGenerations.Should().Be(1);

        release.SetResult("v2");
        await CachePeers.Until(() => _cache.LateGenerations == 0);
        (await _cache.GetAsync<string>(Key, policy, Ct)).Should().Be("v2", "the late value replaced the entry");
        _telemetry.Events.Should().Contain(e => e.Name == "cache.fail_safe.background" && e.Properties!["outcome"] == "stored");
    }

    [Fact]
    public async Task Before_the_soft_timeout_the_generator_is_waited_for()
    {
        var policy = Merge(new CachePolicy { FailSafeTimeout = TimeSpan.FromSeconds(2) });
        (await _cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, policy, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, policy, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(TimeSpan.FromSeconds(1));
        release.SetResult("v2");

        (await pending).Should().Be("v2");
        _telemetry.Events.Should().NotContain(e => e.Name == "cache.fail_safe.served");
    }

    [Fact]
    public async Task Without_a_kept_value_the_soft_timeout_waits()
    {
        var policy = Merge(new CachePolicy { FailSafeTimeout = TimeSpan.FromSeconds(2) });
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, policy, Ct).AsTask();
        _clock.Advance(TimeSpan.FromSeconds(5));
        release.SetResult("v1");

        (await pending).Should().Be("v1");
        _clock.HasTimers.Should().BeFalse("no timer is started when there is nothing to fall back to");
    }

    [Fact]
    public async Task No_value_is_returned_at_the_soft_timeout_once_every_caller_left()
    {
        var policy = Merge(new CachePolicy { FailSafeTimeout = TimeSpan.FromSeconds(2) });
        (await _cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, policy, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();

        // The generator ignores its token, so the run goes on after the caller left.
        var pending = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, policy, caller.Token).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        await caller.CancelAsync();
        await pending.Invoking(p => p).Should().ThrowAsync<OperationCanceledException>();
        _clock.Advance(TimeSpan.FromSeconds(2));
        release.SetResult("v2");
        await CachePeers.Until(() => _cache.RunsInFlight == 0);

        _telemetry.Events.Should().NotContain(e => e.Name == "cache.fail_safe.served", "nobody was waiting for the fallback");
        _cache.LateGenerations.Should().Be(0);
    }

    [Fact]
    public async Task A_late_value_is_discarded_when_the_key_was_removed_meanwhile()
    {
        var policy = Merge(new CachePolicy { FailSafeTimeout = TimeSpan.FromSeconds(2) });
        (await _cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, policy, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, policy, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(TimeSpan.FromSeconds(2));
        (await pending).Should().Be("v1");

        await _cache.RemoveAsync<string>(Key, Ct);
        release.SetResult("from-before-the-removal");
        await CachePeers.Until(() => _cache.LateGenerations == 0);

        (await _cache.GetAsync<string>(Key, policy, Ct)).Should().BeNull();
        _telemetry.Events.Should().Contain(e => e.Name == "cache.fail_safe.background" && e.Properties!["outcome"] == "discarded");
    }

    [Fact]
    public async Task A_late_generator_that_fails_leaves_the_kept_value()
    {
        var policy = Merge(new CachePolicy { FailSafeTimeout = TimeSpan.FromSeconds(2), FailSafeCooldown = TimeSpan.Zero });
        (await _cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, policy, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, policy, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(TimeSpan.FromSeconds(2));
        (await pending).Should().Be("v1");

        release.SetException(new InvalidOperationException("down"));
        await CachePeers.Until(() => _cache.LateGenerations == 0);

        (await _cache.GetOrAddAsync(Key, Throwing, Lifetime, policy, Ct)).Should().Be("v1");
        _telemetry.Events.Should().Contain(e => e.Name == "cache.fail_safe.background" && e.Properties!["outcome"] == "failed");
    }

    [Fact]
    public async Task A_miss_while_a_late_generator_runs_starts_a_generator_of_its_own()
    {
        var policy = Merge(new CachePolicy { FailSafeTimeout = TimeSpan.FromSeconds(2) });
        var first = await LateAsync(policy);
        _clock.Advance(FailSafe.FailSafeCooldown!.Value + TimeSpan.FromSeconds(1));

        (await _cache.GetOrAddAsync(Key, Producing("v2"), Lifetime, policy, Ct)).Should().Be("v2", "the miss asks the source itself and does not wait for the late generator");
        _cache.LateGenerations.Should().Be(1);

        first.SetResult("late");
        await CachePeers.Until(() => _cache.LateGenerations == 0);
        (await _cache.GetAsync<string>(Key, policy, Ct)).Should().Be("v2", "the late generator began before v2 was written");
        _telemetry.Events.Should().Contain(e => e.Name == "cache.fail_safe.background" && e.Properties!["outcome"] == "discarded");
    }

    [Fact]
    public async Task A_late_value_is_discarded_after_a_newer_local_write()
    {
        var policy = Merge(new CachePolicy { FailSafeTimeout = TimeSpan.FromSeconds(2) });
        var release = await LateAsync(policy);

        (await _cache.SetAsync(Key, "newer", Lifetime, policy, Ct)).Should().BeTrue();
        release.SetResult("late");
        await CachePeers.Until(() => _cache.LateGenerations == 0);

        (await _cache.GetAsync<string>(Key, policy, Ct)).Should().Be("newer");
        _telemetry.Events.Should().Contain(e => e.Name == "cache.fail_safe.background" && e.Properties!["outcome"] == "discarded");
    }

    [Fact]
    public async Task A_removal_after_the_late_check_still_wins()
    {
        var policy = Merge(new CachePolicy { FailSafeTimeout = TimeSpan.FromSeconds(2) });
        var release = await LateAsync(policy);

        // The removal lands after the late generator saw its value current and before its store.
        _cache.LateStoreStarting = () => _cache.RemoveAsync<string>(Key, Ct).AsTask().GetAwaiter().GetResult();
        release.SetResult("late");
        await CachePeers.Until(() => _cache.LateGenerations == 0);

        (await _cache.GetAsync<string>(Key, policy, Ct)).Should().BeNull("the removal is newer than the late value");
    }

    [Fact]
    public async Task A_call_without_fail_safe_that_joins_the_run_gets_the_generators_error()
    {
        (await _cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, FailSafe, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opted = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, FailSafe, Ct).AsTask();
        await CachePeers.Until(() => _cache.RunsInFlight == 1);
        var plain = _cache.GetOrAddAsync(Key, Throwing, Lifetime, new CachePolicy(), Ct).AsTask();

        release.SetException(new InvalidOperationException("down"));

        (await opted).Should().Be("v1");
        await plain.Invoking(p => p).Should().ThrowAsync<InvalidOperationException>("a caller without fail-safe is never answered with the kept value");
    }

    [Fact]
    public async Task A_batch_call_that_joins_the_run_gets_the_generators_error()
    {
        (await _cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, FailSafe, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opted = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, FailSafe, Ct).AsTask();
        await CachePeers.Until(() => _cache.RunsInFlight == 1);
        var batch = BatchAsync();

        release.SetException(new InvalidOperationException("down"));

        (await opted).Should().Be("v1");
        await batch.Invoking(b => b).Should().ThrowAsync<InvalidOperationException>("a batch call does not serve the kept value");
    }

    [Fact]
    public async Task A_late_generator_that_ends_after_dispose_logs_no_failure()
    {
        var logger = new CountingLogger();
        var cache = Build(new InMemoryCacheOptions(), logger);
        var policy = Merge(new CachePolicy { FailSafeTimeout = TimeSpan.FromSeconds(2) });
        (await cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, policy, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, policy, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(TimeSpan.FromSeconds(2));
        (await pending).Should().Be("v1");

        var served = logger.Warnings;
        cache.Dispose();
        release.SetResult("late");
        await CachePeers.Until(() => cache.LateGenerations == 0);

        logger.Warnings.Should().Be(served, "a late generator that ends after dispose is not a failure");
        _telemetry.Events.Should().Contain(e => e.Name == "cache.fail_safe.background" && e.Properties!["outcome"] == "discarded");
    }

    [Fact]
    public async Task Under_the_distributed_lock_the_soft_timeout_waits_for_the_generator()
    {
        var locks = new RecordingDistributedLock();
        using var cache = Build(new InMemoryCacheOptions { DistributedLockEnabled = true }, distributedLock: locks);
        var policy = Merge(new CachePolicy { FailSafeTimeout = TimeSpan.FromSeconds(2) });
        (await cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, policy, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, policy, Ct).AsTask();
        _clock.HasTimers.Should().BeFalse("a late generator would outlive the lease, so no soft timeout is armed");
        _clock.Advance(TimeSpan.FromSeconds(5));
        pending.IsCompleted.Should().BeFalse();
        release.SetResult("v2");

        (await pending).Should().Be("v2");
        cache.LateGenerations.Should().Be(0);
        locks.Acquisitions.Should().Be(2);
    }

    [Fact]
    public async Task Under_the_distributed_lock_a_failing_generator_still_falls_back()
    {
        using var cache = Build(new InMemoryCacheOptions { DistributedLockEnabled = true }, distributedLock: new RecordingDistributedLock());
        (await cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, FailSafe, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);

        (await cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct)).Should().Be("v1");
    }

    [Fact]
    public async Task A_size_limited_cache_keeps_values_too()
    {
        using var cache = Build(new InMemoryCacheOptions { SizeLimit = 100 });
        (await cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, FailSafe, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);

        (await cache.GetOrAddAsync(Key, Throwing, Lifetime, FailSafe, Ct)).Should().Be("v1");
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CachePolicy Merge(CachePolicy policy) => CachePolicyMerger.Merge(policy, FailSafe);

    /// <summary>Warms the key, expires it and runs a generator past the soft timeout; the source completes it later.</summary>
    private async Task<TaskCompletionSource<string?>> LateAsync(CachePolicy policy)
    {
        (await _cache.GetOrAddAsync(Key, Producing("v1"), Lifetime, policy, Ct)).Should().Be("v1");
        _clock.Advance(Lifetime * 2);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = _cache.GetOrAddAsync(Key, _ => release.Task, Lifetime, policy, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(TimeSpan.FromSeconds(2));
        (await pending).Should().Be("v1");
        return release;
    }

    private Task<KeyValuePair<long, string?>[]> BatchAsync() =>
        _cache.GetOrAddAsync<string, long>([new(Key, 1L)], static (_, _) => Task.FromException<KeyValuePair<long, string?>[]>(new InvalidOperationException("batch down")), FailSafe, Ct).AsTask();

    private async Task WarmAsync(string value) =>
        (await _cache.GetOrAddAsync(Key, Producing(value), Lifetime, FailSafe, Ct)).Should().Be(value);

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

    private MultilayerCache Build(InMemoryCacheOptions options, Microsoft.Extensions.Logging.ILogger? logger = null, IDistributedLock? distributedLock = null) =>
        RunTables.Track(new MultilayerCache(
            KnownCacheProviderNames.InMemory,
            NullCache.Instance,
            new MemoryCacheFactory(_clock, NullLoggerFactory.Instance),
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
            logger: logger ?? NullLogger.Instance));

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
