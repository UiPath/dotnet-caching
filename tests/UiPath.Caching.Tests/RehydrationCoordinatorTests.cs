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
        var clock = new CacheClock(clock: null, defaultExpiration: Duration);
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
            .Returns<ValueTask<IAsyncDisposable?>>(_ => throw new InvalidOperationException("lock unavailable"));

        var telemetry = new RecordingTelemetryProvider();
        var sut = NewCoordinator(distributedLock, telemetry);
        var aged = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(1));
        var key = (CacheKey)"k";

        var triggered = sut.TryTrigger(key, aged, RehydratePolicy(), Duration, "cache", _ => ValueTask.CompletedTask);

        triggered.Should().BeTrue();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        // _inFlight must be cleared so a follow-up trigger on the same key can proceed.
        var second = sut.TryTrigger(key, aged, RehydratePolicy(), Duration, "cache", _ => ValueTask.CompletedTask);
        second.Should().BeTrue();
    }

    [Fact]
    public void Second_concurrent_TryTrigger_on_same_key_returns_false()
    {
        var distributedLock = Substitute.For<IDistributedLock>();
        var lockHeld = new TaskCompletionSource<IAsyncDisposable?>(TaskCreationOptions.RunContinuationsAsynchronously);
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IAsyncDisposable?>(lockHeld.Task));

        var sut = NewCoordinator(distributedLock);
        var aged = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(1));
        var key = (CacheKey)"k";

        var first = sut.TryTrigger(key, aged, RehydratePolicy(), Duration, "cache", _ => ValueTask.CompletedTask);
        var second = sut.TryTrigger(key, aged, RehydratePolicy(), Duration, "cache", _ => ValueTask.CompletedTask);

        first.Should().BeTrue();
        second.Should().BeFalse("per-node _inFlight blocks duplicate spawns on the same key");
        lockHeld.TrySetResult(Substitute.For<IAsyncDisposable>());
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
