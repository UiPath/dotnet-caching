using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Locking;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests;

public class RehydrationCoordinatorTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);

    private static RehydrationCoordinator NewCoordinator(
        IDistributedLock? distributedLock = null,
        RecordingTelemetryProvider? telemetry = null)
    {
        var clock = TimeProvider.System;
        var lockKeyStrategy = new DefaultDistributedLockKeyStrategy(separator: ':');
        return new RehydrationCoordinator(
            cacheName: "test-cache",
            clock,
            distributedLock ?? NullDistributedLock.Instance,
            lockKeyStrategy,
            telemetry ?? new RecordingTelemetryProvider(),
            NullLogger.Instance);
    }

    private static CachePolicy RehydratePolicy(
        double threshold = 0.5,
        double timeoutFraction = 0.5,
        TimeSpan? baseCooldown = null) => new()
    {
        DistributedExpiration = Duration,
        RehydrateEnabled = true,
        Rehydrate = new RehydrateOptions
        {
            Threshold = threshold,
            BaseCooldown = baseCooldown ?? TimeSpan.FromSeconds(1),
            MaxCooldown = TimeSpan.FromMinutes(5),
            TimeoutFraction = timeoutFraction,
            Name = "test",
        },
    };

    [Fact]
    public void TryTrigger_returns_false_when_RehydrateEnabled_is_null()
    {
        var sut = NewCoordinator();
        var policy = new CachePolicy { RehydrateEnabled = null, Rehydrate = RehydratePolicy().Rehydrate };

        var triggered = sut.TryTrigger((CacheKey)"k", DateTimeOffset.UtcNow.Add(Duration), policy, Duration, "cache", _ => ValueTask.CompletedTask);

        triggered.Should().BeFalse();
    }

    [Fact]
    public void TryTrigger_returns_false_when_Rehydrate_options_is_null()
    {
        var sut = NewCoordinator();
        var policy = new CachePolicy { RehydrateEnabled = true, Rehydrate = null };

        var triggered = sut.TryTrigger((CacheKey)"k", DateTimeOffset.UtcNow.Add(Duration), policy, Duration, "cache", _ => ValueTask.CompletedTask);

        triggered.Should().BeFalse();
    }

    [Fact]
    public void TryTrigger_returns_false_when_duration_is_zero_or_negative()
    {
        var sut = NewCoordinator();

        sut.TryTrigger((CacheKey)"k", DateTimeOffset.UtcNow.Add(Duration), RehydratePolicy(), TimeSpan.Zero, "cache", _ => ValueTask.CompletedTask).Should().BeFalse();
        sut.TryTrigger((CacheKey)"k", DateTimeOffset.UtcNow.Add(Duration), RehydratePolicy(), TimeSpan.FromSeconds(-1), "cache", _ => ValueTask.CompletedTask).Should().BeFalse();
    }

    [Fact]
    public void TryTrigger_returns_false_when_entry_is_already_expired()
    {
        var sut = NewCoordinator();

        var triggered = sut.TryTrigger((CacheKey)"k", DateTimeOffset.UtcNow.AddMinutes(-5), RehydratePolicy(), Duration, "cache", _ => ValueTask.CompletedTask);

        triggered.Should().BeFalse();
    }

    [Fact]
    public void TryTrigger_returns_false_when_elapsedFraction_below_threshold()
    {
        var sut = NewCoordinator();
        var fresh = DateTimeOffset.UtcNow.Add(Duration);

        var triggered = sut.TryTrigger((CacheKey)"k", fresh, RehydratePolicy(threshold: 0.75), Duration, "cache", _ => ValueTask.CompletedTask);

        triggered.Should().BeFalse();
    }

    [Fact]
    public async Task SpawnAsync_emits_timed_out_event_on_timeout()
    {
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IAsyncDisposable>());

        var telemetry = new RecordingTelemetryProvider();
        var sut = NewCoordinator(distributedLock, telemetry);
        var aged = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(1));
        var policy = RehydratePolicy(timeoutFraction: 0.001, baseCooldown: TimeSpan.FromMilliseconds(50));

        var triggered = sut.TryTrigger(
            (CacheKey)"k",
            aged,
            policy,
            Duration,
            "cache",
            async ct => await Task.Delay(TimeSpan.FromSeconds(30), ct));

        triggered.Should().BeTrue();
        await WaitForEvent(telemetry, "cache.rehydrate.timed_out", TimeSpan.FromSeconds(30));
        telemetry.Events.Should().Contain(e => e.Name == "cache.rehydrate.timed_out");
    }

    [Fact]
    public async Task SpawnAsync_emits_failed_event_when_generator_throws()
    {
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IAsyncDisposable>());

        var telemetry = new RecordingTelemetryProvider();
        var sut = NewCoordinator(distributedLock, telemetry);
        var aged = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(1));

        var triggered = sut.TryTrigger(
            (CacheKey)"k",
            aged,
            RehydratePolicy(),
            Duration,
            "cache",
            _ => throw new InvalidOperationException("boom"));

        triggered.Should().BeTrue();
        await WaitForEvent(telemetry, "cache.rehydrate.failed", TimeSpan.FromSeconds(30));
        telemetry.Events.Should().Contain(e => e.Name == "cache.rehydrate.failed");
    }

    [Fact]
    public async Task SpawnAsync_emits_deduped_when_lock_acquire_exceeds_factoryTimeout()
    {
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ct = call.Arg<CancellationToken>();
                return new ValueTask<IAsyncDisposable?>(HangAsync(ct));
            });

        var telemetry = new RecordingTelemetryProvider();
        var sut = NewCoordinator(distributedLock, telemetry);
        var aged = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(1));
        var policy = RehydratePolicy(timeoutFraction: 0.001, baseCooldown: TimeSpan.FromMilliseconds(50));

        var triggered = sut.TryTrigger(
            (CacheKey)"k",
            aged,
            policy,
            Duration,
            "cache",
            _ => ValueTask.CompletedTask);

        triggered.Should().BeTrue();
        await WaitForEvent(telemetry, "cache.rehydrate.deduped", TimeSpan.FromSeconds(30));
        telemetry.Events.Should().Contain(e => e.Name == "cache.rehydrate.deduped");
        var timedOut = telemetry.Events.SingleOrDefault(e => e.Name == "cache.factory.timed_out");
        timedOut.Should().NotBeNull("the FactoryTimeout helper emits timed_out when the acquire exceeds the bound");
        timedOut!.Properties.Should().ContainKey("source").WhoseValue.Should().Be(
            "rehydrate-lock",
            "tagged with source=rehydrate-lock so it doesn't pollute foreground-factory dashboards");

        static async Task<IAsyncDisposable?> HangAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Substitute.For<IAsyncDisposable>();
        }
    }

    [Fact]
    public async Task SpawnAsync_outer_catch_logs_and_releases_inFlight_when_lock_acquire_throws()
    {
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncDisposable?>(_ => throw new InvalidOperationException("lock unavailable"));

        var telemetry = new RecordingTelemetryProvider();
        var sut = NewCoordinator(distributedLock, telemetry);
        var aged = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(1));
        var key = (CacheKey)"k";

        var triggered = sut.TryTrigger(key, aged, RehydratePolicy(), Duration, "cache", _ => ValueTask.CompletedTask);

        triggered.Should().BeTrue();

        var second = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            second = sut.TryTrigger(key, aged, RehydratePolicy(), Duration, "cache", _ => ValueTask.CompletedTask);
            if (second)
            {
                break;
            }
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        second.Should().BeTrue();
    }

    [Fact]
    public void Second_concurrent_TryTrigger_on_same_key_returns_false()
    {
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncDisposable?>(_ => Substitute.For<IAsyncDisposable>());

        var sut = NewCoordinator(distributedLock);
        var aged = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(1));
        var key = (CacheKey)"k";
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = sut.TryTrigger(key, aged, RehydratePolicy(), Duration, "cache", async _ => await gate.Task);
        var second = sut.TryTrigger(key, aged, RehydratePolicy(), Duration, "cache", _ => ValueTask.CompletedTask);

        first.Should().BeTrue();
        second.Should().BeFalse("per-node _inFlight blocks duplicate spawns on the same key");
        gate.TrySetResult();
    }

    [Fact]
    public async Task SpawnAsync_lock_expiry_is_factory_timeout_plus_cooldown()
    {
        // BaseCooldown=1s, factory budget = TimeoutFraction(0.5) * Duration(10min) = 5min.
        // lockExpiry must cover the factory window AND the post-failure cooldown so that
        // BaseCooldown/MaxCooldown actually control retry cadence regardless of how the
        // factory finishes (quick failure vs timeout). Without the additive term, quick
        // failures over-cool (lock holds for full factoryTimeout) and timeouts under-cool
        // (lock TTL elapses while cancellation fires).
        var distributedLock = Substitute.For<IDistributedLock>();
        TimeSpan capturedExpiry = TimeSpan.Zero;
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Do<TimeSpan>(e => capturedExpiry = e), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IAsyncDisposable>());

        var sut = NewCoordinator(distributedLock);
        var aged = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(1));
        var cooldown = TimeSpan.FromSeconds(1);
        var policy = RehydratePolicy(timeoutFraction: 0.5, baseCooldown: cooldown);

        var triggered = sut.TryTrigger(
            (CacheKey)"k",
            aged,
            policy,
            Duration,
            "cache",
            _ => ValueTask.CompletedTask);

        triggered.Should().BeTrue();
        await WaitForCallAsync(() => capturedExpiry > TimeSpan.Zero, TimeSpan.FromSeconds(5));

        var expectedFactoryTimeout = TimeSpan.FromMilliseconds(0.5 * Duration.TotalMilliseconds);
        capturedExpiry.Should().Be(expectedFactoryTimeout + cooldown,
            "lockExpiry = factoryTimeout + cooldown so the failure path holds the lock for the factory window plus the configured cooldown");
    }

    [Fact]
    public async Task Overlapping_reserved_sets_on_two_nodes_rehydrate_the_shared_key_once()
    {
        var granted = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncDisposable?>(c => granted.TryAdd(c.ArgAt<string>(0), 0) ? Substitute.For<IAsyncDisposable>() : null);

        var nodeA = NewCoordinator(distributedLock);
        var nodeB = NewCoordinator(distributedLock);
        var aged = DateTimeOffset.UtcNow.AddMinutes(1);
        var policy = RehydratePolicy();
        var a = (CacheKey)"a";

        var rehydrated = new ConcurrentBag<CacheKey>();
        var ranA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ranB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask Record(CacheKey[] keys, TaskCompletionSource ran)
        {
            foreach (var key in keys) { rehydrated.Add(key); }
            ran.TrySetResult();
            return ValueTask.CompletedTask;
        }

        nodeA.TryTriggerBatch([(a, aged), ((CacheKey)"b", aged)], policy, Duration, "cache", (keys, _) => Record(keys, ranA))
            .Should().BeTrue();
        nodeB.TryTriggerBatch([(a, aged), ((CacheKey)"c", aged)], policy, Duration, "cache", (keys, _) => Record(keys, ranB))
            .Should().BeTrue();

        await ranA.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await ranB.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var all = rehydrated.ToArray();
        all.Count(k => k == a).Should().Be(
            1,
            "per-key locks make the shared key dedupe across nodes even though the reserved sets are not equal");
        all.Should().Contain((CacheKey)"b").And.Contain((CacheKey)"c", "each node still refreshes the key only it reserved");
    }

    [Fact]
    public async Task Batch_cooldown_uses_the_max_failure_count_across_the_set()
    {
        var expiries = new ConcurrentDictionary<string, TimeSpan>(StringComparer.Ordinal);
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                expiries[c.ArgAt<string>(0)] = c.ArgAt<TimeSpan>(1);
                return Substitute.For<IAsyncDisposable>();
            });

        var sut = NewCoordinator(distributedLock);
        var aged = DateTimeOffset.UtcNow.AddMinutes(1);
        var baseCooldown = TimeSpan.FromSeconds(1);
        var policy = RehydratePolicy(baseCooldown: baseCooldown);
        var lockKeyStrategy = new DefaultDistributedLockKeyStrategy(separator: ':');
        var failing = (CacheKey)"failing";

        sut.TryTrigger(failing, aged, policy, Duration, "cache", _ => throw new InvalidOperationException("boom"))
            .Should().BeTrue();

        var expectedFactoryTimeout = TimeSpan.FromMilliseconds(0.5 * Duration.TotalMilliseconds);
        var expectedCooldown = baseCooldown * 2;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        for (var attempt = 0; DateTime.UtcNow < deadline; attempt++)
        {
            CacheKey[]? rehydrated = null;
            var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sut.TryTriggerBatch(
                [(failing, aged), ((CacheKey)("never-failed-" + attempt), aged)],
                policy,
                Duration,
                "cache",
                (keys, _) => { rehydrated = keys; ran.TrySetResult(); return ValueTask.CompletedTask; })
                .Should().BeTrue("the partner key has never been seen, so it is always reservable");
            await ran.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            if (rehydrated!.Contains(failing))
            {
                var partner = rehydrated!.Single(k => k != failing);
                foreach (var key in rehydrated!)
                {
                    var lockKey = "rehydrate:" + lockKeyStrategy.GetLockKey(key);
                    expiries.Should().ContainKey(lockKey, "each reserved key takes its own rehydrate lock");
                    expiries[lockKey].Should().Be(
                        expectedFactoryTimeout + expectedCooldown,
                        "the group backs off on the worst key's failure count, not the partner key's zero");
                }
                expiries.Should().NotContainKey(
                    "rehydrate:" + lockKeyStrategy.GetLockKey(CompositeCacheKey.For(rehydrated!)),
                    "the group is no longer locked under a composite key");
                partner.Should().NotBe(failing);
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Fail("\"failing\" never left the in-flight set, so the max-failure-count path was never exercised.");
    }

    private static async Task WaitForCallAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"Predicate was not satisfied within {timeout}.");
    }

    private static async Task WaitForEvent(RecordingTelemetryProvider telemetry, string eventName, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (telemetry.Events.Any(e => e.Name == eventName))
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"Event '{eventName}' was not emitted within {timeout}.");
    }
}
