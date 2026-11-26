using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests;

public class RehydrationCoordinatorTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void TryTrigger_returns_false_when_RehydrateEnabled_is_null()
    {
        var sut = NewCoordinator();
        var policy = new CachePolicy { RehydrateEnabled = null, Rehydrate = RehydratePolicy().Rehydrate };

        var triggered = sut.TryTrigger((CacheKey)"k", _clock.GetUtcNow().Add(Duration), policy, Duration, "cache", (_, _) => ValueTask.CompletedTask);

        triggered.Should().BeFalse();
    }

    [Fact]
    public void TryTrigger_returns_false_when_Rehydrate_options_is_null()
    {
        var sut = NewCoordinator();
        var policy = new CachePolicy { RehydrateEnabled = true, Rehydrate = null };

        var triggered = sut.TryTrigger((CacheKey)"k", _clock.GetUtcNow().Add(Duration), policy, Duration, "cache", (_, _) => ValueTask.CompletedTask);

        triggered.Should().BeFalse();
    }

    [Fact]
    public void TryTrigger_returns_false_when_duration_is_zero_or_negative()
    {
        var sut = NewCoordinator();

        sut.TryTrigger((CacheKey)"k", _clock.GetUtcNow().Add(Duration), RehydratePolicy(), TimeSpan.Zero, "cache", (_, _) => ValueTask.CompletedTask).Should().BeFalse();
        sut.TryTrigger((CacheKey)"k", _clock.GetUtcNow().Add(Duration), RehydratePolicy(), TimeSpan.FromSeconds(-1), "cache", (_, _) => ValueTask.CompletedTask).Should().BeFalse();
    }

    [Fact]
    public void TryTrigger_returns_false_when_entry_is_already_expired()
    {
        var sut = NewCoordinator();

        var triggered = sut.TryTrigger((CacheKey)"k", _clock.GetUtcNow().AddMinutes(-5), RehydratePolicy(), Duration, "cache", (_, _) => ValueTask.CompletedTask);

        triggered.Should().BeFalse();
    }

    [Fact]
    public void TryTrigger_returns_false_when_elapsedFraction_below_threshold()
    {
        var sut = NewCoordinator();
        var fresh = _clock.GetUtcNow().Add(Duration);

        var triggered = sut.TryTrigger((CacheKey)"k", fresh, RehydratePolicy(threshold: 0.75), Duration, "cache", (_, _) => ValueTask.CompletedTask);

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
        var aged = _clock.GetUtcNow().Add(TimeSpan.FromSeconds(1));
        var policy = RehydratePolicy(timeoutFraction: 0.001, baseCooldown: TimeSpan.FromMilliseconds(50));

        var triggered = sut.TryTrigger(
            (CacheKey)"k",
            aged,
            policy,
            Duration,
            "cache",
            async (_, ct) => await new TaskCompletionSource().Task.WaitAsync(ct));

        triggered.Should().BeTrue();
        _clock.Advance(TimeSpan.FromSeconds(2));
        await sut.Completion;
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
        var aged = _clock.GetUtcNow().Add(TimeSpan.FromSeconds(1));

        var triggered = sut.TryTrigger(
            (CacheKey)"k",
            aged,
            RehydratePolicy(),
            Duration,
            "cache",
            (_, _) => throw new InvalidOperationException("boom"));

        triggered.Should().BeTrue();
        await sut.Completion;
        telemetry.Events.Should().Contain(e => e.Name == "cache.rehydrate.failed");
    }

    [Fact]
    public async Task SpawnAsync_emits_deduped_when_lock_acquire_exceeds_factoryTimeout()
    {
        var distributedLock = new HangingLock();

        var telemetry = new RecordingTelemetryProvider();
        var sut = NewCoordinator(distributedLock, telemetry);
        var aged = _clock.GetUtcNow().Add(TimeSpan.FromSeconds(1));
        var policy = RehydratePolicy(timeoutFraction: 0.001, baseCooldown: TimeSpan.FromMilliseconds(50));

        var triggered = sut.TryTrigger(
            (CacheKey)"k",
            aged,
            policy,
            Duration,
            "cache",
            (_, _) => ValueTask.CompletedTask);

        triggered.Should().BeTrue();
        _clock.Advance(TimeSpan.FromSeconds(1));
        await sut.Completion;
        telemetry.Events.Should().Contain(e => e.Name == "cache.rehydrate.deduped");
        var timedOut = telemetry.Events.SingleOrDefault(e => e.Name == "cache.factory.timed_out");
        timedOut.Should().NotBeNull("the FactoryTimeout helper emits timed_out when the acquire exceeds the bound");
        timedOut!.Properties.Should().ContainKey("source").WhoseValue.Should().Be(
            "rehydrate-lock",
            "tagged with source=rehydrate-lock so it doesn't pollute foreground-factory dashboards");
    }

    [Fact]
    public async Task SpawnAsync_outer_catch_logs_and_releases_inFlight_when_lock_acquire_throws()
    {
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncDisposable?>(_ => throw new InvalidOperationException("lock unavailable"));

        var telemetry = new RecordingTelemetryProvider();
        var sut = NewCoordinator(distributedLock, telemetry);
        var aged = _clock.GetUtcNow().Add(TimeSpan.FromSeconds(1));
        var key = (CacheKey)"k";

        var triggered = sut.TryTrigger(key, aged, RehydratePolicy(), Duration, "cache", (_, _) => ValueTask.CompletedTask);

        triggered.Should().BeTrue();

        await sut.Completion;
        var second = sut.TryTrigger(key, aged, RehydratePolicy(), Duration, "cache", (_, _) => ValueTask.CompletedTask);

        second.Should().BeTrue();
    }

    [Fact]
    public void Second_concurrent_TryTrigger_on_same_key_returns_false()
    {
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncDisposable?>(_ => Substitute.For<IAsyncDisposable>());

        var sut = NewCoordinator(distributedLock);
        var aged = _clock.GetUtcNow().Add(TimeSpan.FromSeconds(1));
        var key = (CacheKey)"k";
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = sut.TryTrigger(key, aged, RehydratePolicy(), Duration, "cache", async (_, _) => await gate.Task);
        var second = sut.TryTrigger(key, aged, RehydratePolicy(), Duration, "cache", (_, _) => ValueTask.CompletedTask);

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
        var aged = _clock.GetUtcNow().Add(TimeSpan.FromSeconds(1));
        var cooldown = TimeSpan.FromSeconds(1);
        var policy = RehydratePolicy(timeoutFraction: 0.5, baseCooldown: cooldown);

        var triggered = sut.TryTrigger(
            (CacheKey)"k",
            aged,
            policy,
            Duration,
            "cache",
            (_, _) => ValueTask.CompletedTask);

        triggered.Should().BeTrue();
        await sut.Completion;

        var expectedFactoryTimeout = TimeSpan.FromSeconds(302);
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
        var aged = _clock.GetUtcNow().AddMinutes(1);
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

        nodeA.TryTriggerBatch([(a, aged), ((CacheKey)"b", aged)], policy, Duration, "cache", (keys, _, _) => Record(keys, ranA))
            .Should().BeTrue();
        nodeB.TryTriggerBatch([(a, aged), ((CacheKey)"c", aged)], policy, Duration, "cache", (keys, _, _) => Record(keys, ranB))
            .Should().BeTrue();

        await ranA.Task.WaitAsync(TestContext.Current.CancellationToken);
        await ranB.Task.WaitAsync(TestContext.Current.CancellationToken);

        var all = rehydrated.ToArray();
        all.Count(k => k == a).Should().Be(
            1,
            "per-key locks make the shared key dedupe across nodes even though the reserved sets are not equal");
        all.Should().Contain((CacheKey)"b").And.Contain((CacheKey)"c", "each node still refreshes the key only it reserved");
    }

    [Fact]
    public async Task Batch_cooldown_uses_each_keys_own_failure_count()
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
        var aged = _clock.GetUtcNow().AddMinutes(1);
        var baseCooldown = TimeSpan.FromSeconds(1);
        var policy = RehydratePolicy(baseCooldown: baseCooldown);
        var lockKeyStrategy = new DefaultDistributedLockKeyStrategy(separator: ':');
        var failing = (CacheKey)"failing";

        sut.TryTrigger(failing, aged, policy, Duration, "cache", (_, _) => throw new InvalidOperationException("boom"))
            .Should().BeTrue();

        var expectedFactoryTimeout = TimeSpan.FromSeconds(302);
        var expectedCooldown = baseCooldown * 2;
        await sut.Completion;
        CacheKey[]? rehydrated = null;
        var partner = (CacheKey)"never-failed";
        sut.TryTriggerBatch(
            [(failing, aged), (partner, aged)],
            policy,
            Duration,
            "cache",
            (keys, _, _) => { rehydrated = keys; return ValueTask.CompletedTask; }).Should().BeTrue();
        await sut.Completion;
        rehydrated.Should().BeEquivalentTo(new[] { failing, partner });
        foreach (var key in rehydrated!)
        {
            var lockKey = "rehydrate:" + lockKeyStrategy.GetLockKey(key);
            expiries.Should().ContainKey(lockKey);
            expiries[lockKey].Should().Be(expectedFactoryTimeout + (key == failing ? expectedCooldown : baseCooldown));
        }
        expiries.Should().NotContainKey("rehydrate:" + lockKeyStrategy.GetLockKey(CompositeCacheKey.For(rehydrated!)));
    }

    [Fact]
    public async Task SpawnAsync_still_rehydrates_when_the_telemetry_sink_refuses_the_triggered_event()
    {
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IAsyncDisposable>());

        var telemetry = new RefusingTelemetryProvider("cache.rehydrate.triggered");
        var sut = NewCoordinator(distributedLock, telemetry);
        var rehydrated = new TaskCompletionSource();

        var triggered = sut.TryTrigger(
            (CacheKey)"k",
            _clock.GetUtcNow().Add(TimeSpan.FromSeconds(1)),
            RehydratePolicy(),
            Duration,
            "cache",
            (_, _) =>
            {
                rehydrated.TrySetResult();
                return ValueTask.CompletedTask;
            });

        triggered.Should().BeTrue();
        await rehydrated.Task.WaitAsync(TestContext.Current.CancellationToken);
        telemetry.Exceptions.Should().Contain(RefusingTelemetryProvider.Failure, "the refusal is reported rather than swallowed");
    }

    [Theory]
    [InlineData("cache.rehydrate.timed_out")]
    [InlineData("cache.rehydrate.failed")]
    public async Task SpawnAsync_keeps_the_locks_for_the_cooldown_when_the_telemetry_sink_refuses_the_outcome_event(string refusedEvent)
    {
        // The draining probe gets its own handle, or it would dispose the one under test.
        var handle = Substitute.For<IAsyncDisposable>();
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(handle, Substitute.For<IAsyncDisposable>());

        var telemetry = new RefusingTelemetryProvider(refusedEvent);
        var logger = new CapturingLogger();
        var sut = NewCoordinator(distributedLock, telemetry, logger);
        var policy = RehydratePolicy(timeoutFraction: 0.001, baseCooldown: TimeSpan.FromMilliseconds(50));

        var triggered = sut.TryTrigger(
            (CacheKey)"k",
            _clock.GetUtcNow().Add(TimeSpan.FromSeconds(1)),
            policy,
            Duration,
            "cache",
            refusedEvent == "cache.rehydrate.failed"
                ? (_, _) => ValueTask.FromException(new InvalidOperationException("factory failed"))
                : async (_, ct) => await new TaskCompletionSource().Task.WaitAsync(ct));

        triggered.Should().BeTrue();
        _clock.Advance(TimeSpan.FromSeconds(2));
        await sut.Completion;
        telemetry.Exceptions.Should().Contain(RefusingTelemetryProvider.Failure);

        // Same key, since _inFlight is keyed by name. An escaped refusal is logged before the finally clears the
        // reservation, so once it clears the outer catch has already run.
        sut.TryTrigger((CacheKey)"k", _clock.GetUtcNow().AddSeconds(1), policy, Duration, "cache", (_, _) => ValueTask.CompletedTask).Should().BeTrue();
        await sut.Completion;

        logger.Errors.Should().BeEmpty("a refused report must not reach the outer catch, which is what lets the finally release the locks");
        await handle.DidNotReceive().DisposeAsync();
    }

    [Fact]
    public async Task A_failed_run_that_adopted_a_key_releases_its_lock_and_penalises_only_the_generated_key()
    {
        var telemetry = new RecordingTelemetryProvider();
        var handles = new ConcurrentDictionary<string, IAsyncDisposable>();
        var expiries = new ConcurrentDictionary<string, TimeSpan>();
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncDisposable?>(call =>
            {
                expiries[call.Arg<string>()] = call.Arg<TimeSpan>();
                return handles.GetOrAdd(call.Arg<string>(), _ => Substitute.For<IAsyncDisposable>());
            });
        var sut = NewCoordinator(distributedLock, telemetry);
        var policy = RehydratePolicy();
        var aged = _clock.GetUtcNow().Add(TimeSpan.FromSeconds(1));
        var (adopted, generated) = ((CacheKey)"adopted", (CacheKey)"generated");

        sut.TryTriggerBatch(
            [(adopted, aged), (generated, aged)],
            policy,
            Duration,
            "cache",
            (_, run, _) =>
            {
                run.Adopt(adopted);
                throw new InvalidOperationException("upstream is down");
            }).Should().BeTrue();

        await sut.Completion;
        telemetry.Events.Single(e => e.Name == "cache.rehydrate.failed").Properties!["adopted"].Should().Be("1");
        var lockOfAdopted = handles.Single(h => h.Key.Contains("adopted", StringComparison.Ordinal)).Value;
        var lockOfGenerated = handles.Single(h => h.Key.Contains("generated", StringComparison.Ordinal)).Value;
        await lockOfAdopted.Received(1).DisposeAsync();
        await lockOfGenerated.DidNotReceive().DisposeAsync();

        // Each key backs off on its own failure count: the adopted one starts over, the generated one doubles.
        var runTimeout = TimeSpan.FromSeconds(302);
        var baseCooldown = policy.Rehydrate!.BaseCooldown;
        await AssertExpiryAsync(adopted, runTimeout + baseCooldown);
        await AssertExpiryAsync(generated, runTimeout + (baseCooldown * 2));

        async Task AssertExpiryAsync(CacheKey key, TimeSpan expected)
        {
            var lockKey = expiries.Keys.Single(k => k.Contains(key.Name, StringComparison.Ordinal));
            expiries.TryRemove(lockKey, out _);
            sut.TryTrigger(key, aged, policy, Duration, "cache", (_, _) => ValueTask.CompletedTask).Should().BeTrue();
            await sut.Completion;
            expiries[lockKey].Should().Be(expected);
        }
    }

    [Fact]
    public async Task A_key_adopted_beside_a_failure_keeps_its_own_cooldown_and_the_generated_keys_lock_its_own()
    {
        var telemetry = new RecordingTelemetryProvider();
        var expiries = new ConcurrentDictionary<string, TimeSpan>();
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncDisposable?>(call =>
            {
                expiries[call.Arg<string>()] = call.Arg<TimeSpan>();
                return Substitute.For<IAsyncDisposable>();
            });
        var sut = NewCoordinator(distributedLock, telemetry);
        var policy = RehydratePolicy(baseCooldown: TimeSpan.FromSeconds(1), maxCooldown: TimeSpan.FromSeconds(8));
        var aged = _clock.GetUtcNow().Add(TimeSpan.FromSeconds(1));
        var (backedOff, fresh) = ((CacheKey)"backed-off", (CacheKey)"fresh");

        // "backed-off" fails often enough to reach the 8 s cap; "fresh" has never failed.
        for (var failures = 1; failures <= 4; failures++)
        {
            sut.TryTrigger(backedOff, aged, policy, Duration, "cache", (_, _) => throw new InvalidOperationException("boom")).Should().BeTrue();
            await sut.Completion;
            telemetry.Events.Count(e => e.Name == "cache.rehydrate.failed").Should().Be(failures);
        }

        sut.TryTriggerBatch(
            [(backedOff, aged), (fresh, aged)],
            policy,
            Duration,
            "cache",
            (_, run, _) =>
            {
                run.Adopt(backedOff);
                throw new InvalidOperationException("upstream is down");
            }).Should().BeTrue();
        await sut.Completion;
        telemetry.Events.Count(e => e.Name == "cache.rehydrate.failed").Should().Be(5);

        var lockOfFresh = expiries.Single(e => e.Key.Contains("fresh", StringComparison.Ordinal));
        lockOfFresh.Value.Should().Be(TimeSpan.FromSeconds(302 + 1), "the retained lock reflects the generated key's own count of zero, not the adopted key's cap");
        expiries.Single(e => e.Key.Contains("backed-off", StringComparison.Ordinal)).Value.Should().Be(TimeSpan.FromSeconds(302 + 8));
    }

    [Theory]
    [InlineData(2_000, 1_250)]
    [InlineData(20_000, 12_000)]
    public async Task The_run_is_cancelled_at_the_factory_timeout_plus_the_read_budget(int durationMs, int expectedMs)
    {
        var time = new ManualTimeProvider(_clock.GetUtcNow());
        var telemetry = new RecordingTelemetryProvider();
        var sut = NewCoordinator(telemetry: telemetry, time: time);
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);

        sut.TryTrigger(
            (CacheKey)"k",
            time.GetUtcNow() + (duration / 10),
            RehydratePolicy(),
            duration,
            "cache",
            async (_, ct) =>
            {
                started.TrySetResult(ct);
                await new TaskCompletionSource().Task.WaitAsync(ct);
            }).Should().BeTrue();
        var token = await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMilliseconds(expectedMs - 1));
        token.IsCancellationRequested.Should().BeFalse("the read budget is added on top of the factory timeout");
        time.Advance(TimeSpan.FromMilliseconds(1));
        token.IsCancellationRequested.Should().BeTrue();
        await sut.Completion;
        telemetry.Events.Should().Contain(e => e.Name == "cache.rehydrate.timed_out");
    }

    private static CachePolicy RehydratePolicy(
        double threshold = 0.5,
        double timeoutFraction = 0.5,
        TimeSpan? baseCooldown = null,
        TimeSpan? maxCooldown = null) => new()
    {
        DistributedExpiration = Duration,
        RehydrateEnabled = true,
        Rehydrate = new RehydrateOptions
        {
            Threshold = threshold,
            BaseCooldown = baseCooldown ?? TimeSpan.FromSeconds(1),
            MaxCooldown = maxCooldown ?? TimeSpan.FromMinutes(5),
            TimeoutFraction = timeoutFraction,
            Name = "test",
        },
    };

    private ObservedCoordinator NewCoordinator(
        IDistributedLock? distributedLock = null,
        ICachingTelemetryProvider? telemetry = null,
        ILogger? logger = null,
        TimeProvider? time = null)
    {
        var clock = time ?? _clock;
        var lockKeyStrategy = new DefaultDistributedLockKeyStrategy(separator: ':');
        return new ObservedCoordinator(new RehydrationCoordinator(
            cacheName: "test-cache",
            clock,
            distributedLock ?? NullDistributedLock.Instance,
            lockKeyStrategy,
            telemetry ?? new RecordingTelemetryProvider(),
            logger ?? NullLogger.Instance));
    }

    private sealed class ObservedCoordinator(RehydrationCoordinator coordinator)
    {
        public Task Completion { get; private set; } = Task.CompletedTask;

        public bool TryTrigger(CacheKey key, DateTimeOffset expiration, CachePolicy policy, TimeSpan duration, string kind, Func<RehydrateRun, CancellationToken, ValueTask> run) =>
            TryTriggerBatch([(key, expiration)], policy, duration, kind, (_, state, token) => run(state, token));

        public bool TryTriggerBatch(IReadOnlyList<(CacheKey Key, DateTimeOffset Expiration)> candidates, CachePolicy policy, TimeSpan duration, string kind, Func<CacheKey[], RehydrateRun, CancellationToken, ValueTask> run)
        {
            var accepted = coordinator.TryTriggerCore(candidates, policy, duration, kind, run, null, out var completion);
            if (accepted)
            {
                Completion = completion;
            }
            return accepted;
        }
    }

    private sealed class HangingLock : IDistributedLock
    {
        private readonly TaskCompletionSource<IAsyncDisposable?> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IAsyncDisposable> AcquireAsync(string key, TimeSpan expiry, TimeSpan waitTimeout, CancellationToken token) => throw new NotSupportedException();

        public ValueTask<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan expiry, CancellationToken token) =>
            new(_pending.Task.WaitAsync(token));
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _errors = new();

        public IReadOnlyCollection<string> Errors => _errors;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                _errors.Enqueue(formatter(state, exception));
            }
        }
    }
}
