using StackExchange.Redis;
using UiPath.Platform.Caching.Locking;
using UiPath.Platform.Caching.Redis;
using UiPath.Platform.Caching.Telemetry;
using UiPath.Platform.Caching.Tests.Telemetry;

namespace UiPath.Platform.Caching.Tests.Locking;

public class RedisDistributedLockTests(ITestContextAccessor testContextAccessor)
{
    private const string OperationRelease = "distributedlock.release";
    private const string PropOperation = "operation";

    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private RedisDistributedLock NewLock(IRedisConnector? redis = null, CacheOptions? options = null, ICachingTelemetryProvider? telemetry = null)
    {
        redis ??= _fixture.Freeze<IRedisConnector>();
        var opts = Options.Create(options ?? new CacheOptions());
        return new RedisDistributedLock(redis, opts, telemetry ?? NullTelemetryProvider.Instance);
    }

    [Fact]
    public async Task Acquire_returns_disposable_when_LockTake_succeeds()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        var db = redis.Database;
        db.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(true);

        var sut = NewLock(redis);
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.AcquireAsync("k", TimeSpan.FromSeconds(5), TimeSpan.Zero, token);

        lease.Should().NotBeNull();
        await lease!.DisposeAsync();

        await db.Received().LockReleaseAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Acquire_returns_no_op_disposable_when_LockTake_fails_and_wait_is_zero()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Database
            .LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(false);

        var sut = NewLock(redis);
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.AcquireAsync("k", TimeSpan.FromSeconds(5), TimeSpan.Zero, token);

        lease.Should().BeSameAs(NoOpAsyncDisposable.Instance);
        await lease.DisposeAsync();
        await redis.Database.DidNotReceive().LockReleaseAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Acquire_returns_no_op_disposable_when_Redis_throws()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Database
            .LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns<Task<bool>>(_ => throw new RedisException("simulated"));

        var sut = NewLock(redis);
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.AcquireAsync("k", TimeSpan.FromSeconds(5), TimeSpan.Zero, token);

        lease.Should().BeSameAs(NoOpAsyncDisposable.Instance);
    }

    [Fact]
    public async Task TryAcquire_returns_releaser_when_LockTake_succeeds()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        var db = redis.Database;
        db.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(true);

        var sut = NewLock(redis);
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.TryAcquireAsync("k", TimeSpan.FromSeconds(5), token);

        lease.Should().NotBeNull();
        await lease!.DisposeAsync();
        await db.Received().LockReleaseAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task TryAcquire_returns_null_when_LockTake_fails()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Database
            .LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(false);

        var sut = NewLock(redis);
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.TryAcquireAsync("k", TimeSpan.FromSeconds(5), token);

        lease.Should().BeNull("a null return must unambiguously signal not-acquired without sentinel comparison");
        await redis.Database.DidNotReceive().LockReleaseAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task TryAcquire_returns_null_when_Redis_throws_and_tracks_unavailable()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Database
            .LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns<Task<bool>>(_ => throw new RedisException("simulated"));

        var telemetry = new RecordingTelemetryProvider();
        var sut = NewLock(redis, telemetry: telemetry);
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.TryAcquireAsync("k", TimeSpan.FromSeconds(5), token);

        lease.Should().BeNull("Redis unavailability is distinguishable from contention but for the rehydrate coordinator both map to not-acquired — telemetry preserves the distinction");
        telemetry.Events.Should().Contain(e => e.Name == "cache.distributedlock.unavailable");
    }

    [Fact]
    public async Task Acquire_eventually_times_out_when_lock_is_contended()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Database
            .LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(false);

        var sut = NewLock(redis);
        var token = testContextAccessor.Current.CancellationToken;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lease = await sut.AcquireAsync("k", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200), token);
        sw.Stop();

        lease.Should().BeSameAs(NoOpAsyncDisposable.Instance);
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(180, "the locker should retry until the wait timeout elapses");
    }

    [Fact]
    public async Task Acquire_does_not_emit_timeout_event_when_wait_is_zero_and_lock_is_contended()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Database
            .LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(false);

        var telemetry = new RecordingTelemetryProvider();
        var sut = NewLock(redis, telemetry: telemetry);
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.AcquireAsync("k", TimeSpan.FromSeconds(5), TimeSpan.Zero, token);

        lease.Should().BeSameAs(NoOpAsyncDisposable.Instance);
        telemetry.Events.Should().NotContain(e => e.Name == "cache.distributedlock.timeout",
            "a single non-blocking try-acquire is not a timeout — callers that need to distinguish acquired from not-acquired should use TryAcquireAsync (clean null return) rather than the no-op sentinel exposed here");
    }

    [Fact]
    public async Task Acquire_emits_timeout_event_when_positive_wait_elapses()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Database
            .LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(false);

        var telemetry = new RecordingTelemetryProvider();
        var sut = NewLock(redis, telemetry: telemetry);
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.AcquireAsync("k", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150), token);

        lease.Should().BeSameAs(NoOpAsyncDisposable.Instance);
        telemetry.Events.Should().Contain(e => e.Name == "cache.distributedlock.timeout");
    }

    [Theory]
    [InlineData(50, 500, 100)]    // 50 → 100 (doubles, under cap)
    [InlineData(100, 500, 200)]   // 100 → 200
    [InlineData(200, 500, 400)]   // 200 → 400
    [InlineData(400, 500, 500)]   // 400 → min(800, 500) = 500 (caps at max)
    [InlineData(500, 500, 500)]   // already at cap → stays at cap
    [InlineData(700, 500, 500)]   // above cap → coerces down to cap
    public void NextPollInterval_doubles_until_clamped_at_max(int currentMs, int maxMs, int expectedMs)
    {
        var next = RedisDistributedLock.NextPollInterval(TimeSpan.FromMilliseconds(currentMs), TimeSpan.FromMilliseconds(maxMs));
        next.Should().Be(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Fact]
    public void NextPollInterval_doubles_geometrically_until_cap()
    {
        var max = TimeSpan.FromMilliseconds(500);
        var sequence = new List<TimeSpan>();
        var current = TimeSpan.FromMilliseconds(50);
        for (int i = 0; i < 10; i++)
        {
            sequence.Add(current);
            current = RedisDistributedLock.NextPollInterval(current, max);
        }

        sequence.Take(5).Should().Equal(
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(400),
            TimeSpan.FromMilliseconds(500));
        sequence.Skip(5).Should().AllBeEquivalentTo(TimeSpan.FromMilliseconds(500), "the sequence must remain at the cap once reached, never collapsing back to the initial interval");
    }

    [Theory]
    [InlineData(0.0, 40)]    // jitter floor: 50 * 0.8 = 40
    [InlineData(0.5, 50)]    // mid: 50 * 1.0 = 50
    [InlineData(1.0, 60)]    // jitter ceiling: 50 * 1.2 = 60
    public void ComputeRetryDelay_applies_jitter_within_80_to_120_percent(double jitterUnit, int expectedMs)
    {
        var delay = RedisDistributedLock.ComputeRetryDelayWithJitter(
            hasDeadline: true,
            startTimestamp: System.Diagnostics.Stopwatch.GetTimestamp(),
            waitTimeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(50),
            jitterUnit: jitterUnit);
        delay.TotalMilliseconds.Should().BeApproximately(expectedMs, precision: 5);
    }

    [Fact]
    public void ComputeRetryDelay_clamps_to_remaining_budget()
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency; // ~1s in the past
        var delay = RedisDistributedLock.ComputeRetryDelayWithJitter(
            hasDeadline: true,
            startTimestamp: start,
            waitTimeout: TimeSpan.FromMilliseconds(1100),
            pollInterval: TimeSpan.FromMilliseconds(500),
            jitterUnit: 0.5);
        delay.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(200),
            "remaining budget (~100ms) is smaller than jittered poll interval (~500ms), so the delay must clamp to remaining");
    }

    [Fact]
    public void ComputeRetryDelay_returns_zero_when_no_deadline()
    {
        var delay = RedisDistributedLock.ComputeRetryDelayWithJitter(
            hasDeadline: false,
            startTimestamp: 0,
            waitTimeout: TimeSpan.Zero,
            pollInterval: TimeSpan.FromMilliseconds(50),
            jitterUnit: 0.5);
        delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ComputeRetryDelay_returns_zero_when_deadline_already_exceeded()
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp() - 10 * System.Diagnostics.Stopwatch.Frequency;
        var delay = RedisDistributedLock.ComputeRetryDelayWithJitter(
            hasDeadline: true,
            startTimestamp: start,
            waitTimeout: TimeSpan.FromMilliseconds(500),
            pollInterval: TimeSpan.FromMilliseconds(50),
            jitterUnit: 0.5);
        delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task Acquire_retries_at_least_once_when_lock_is_contended()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        var callCount = 0;
        redis.Database
            .LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(_ => { Interlocked.Increment(ref callCount); return false; });

        var sut = NewLock(redis);
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.AcquireAsync("k", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(300), token);

        lease.Should().BeSameAs(NoOpAsyncDisposable.Instance);
        callCount.Should().BeGreaterThanOrEqualTo(2, "the retry loop must fire at least once before giving up");
    }

    [Fact]
    public async Task Lock_token_is_prefixed_with_SourceUri()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        var sourceUri = new Uri("urn:test-host");
        var options = new CacheOptions { SourceUri = sourceUri };

        RedisValue capturedToken = default;
        redis.Database
            .LockTakeAsync(Arg.Any<RedisKey>(), Arg.Do<RedisValue>(v => capturedToken = v), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(true);

        var sut = NewLock(redis, options);
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.AcquireAsync("k", TimeSpan.FromSeconds(5), TimeSpan.Zero, token);
        lease.Should().NotBeNull();

        capturedToken.ToString().Should().StartWith("urn:test-host:", "the lock token must carry the configured source URI for diagnostics");

        await lease!.DisposeAsync();
        await redis.Database
            .Received(1)
            .LockReleaseAsync(Arg.Any<RedisKey>(), Arg.Is<RedisValue>(v => v == capturedToken), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Disposing_lease_swallows_LockRelease_exception_and_tracks_it()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Database
            .LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(true);
        var releaseException = new RedisException("release-failed");
        redis.Database
            .LockReleaseAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns<Task<bool>>(_ => throw releaseException);

        var telemetry = new RecordingTelemetryProvider();
        var sut = NewLock(redis, telemetry: telemetry);
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.AcquireAsync("k", TimeSpan.FromSeconds(5), TimeSpan.Zero, token);

        Func<Task> act = async () => await lease!.DisposeAsync();
        await act.Should().NotThrowAsync("LockReleaseAsync failures must not leak out of DisposeAsync");

        telemetry.Exceptions.Should().ContainSingle()
            .Which.Should().Match<ExceptionRecord>(r =>
                r.Exception == releaseException &&
                r.Properties!.ContainsKey(PropOperation) &&
                r.Properties[PropOperation] == OperationRelease);
    }

    [Fact]
    public async Task Disposing_lease_twice_does_not_release_twice()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Database
            .LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(true);

        var sut = NewLock(redis);
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.AcquireAsync("k", TimeSpan.FromSeconds(5), TimeSpan.Zero, token);
        await lease!.DisposeAsync();
        await lease.DisposeAsync();

        await redis.Database
            .Received(1)
            .LockReleaseAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public void Ctor_throws_when_DistributedLockPollInterval_is_zero_or_negative()
    {
        var act = () => NewLock(options: new CacheOptions { DistributedLockPollInterval = TimeSpan.Zero });
        var ex = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        ex.ParamName.Should().Be("cacheOptions.DistributedLockPollInterval");
    }

    [Fact]
    public void Ctor_throws_when_DistributedLockMaxPollInterval_is_less_than_DistributedLockPollInterval()
    {
        var act = () => NewLock(options: new CacheOptions
        {
            DistributedLockPollInterval = TimeSpan.FromMilliseconds(100),
            DistributedLockMaxPollInterval = TimeSpan.FromMilliseconds(50),
        });
        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("cacheOptions.DistributedLockMaxPollInterval");
    }
}
