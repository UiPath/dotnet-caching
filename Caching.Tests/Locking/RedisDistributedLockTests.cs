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
    public async Task Acquire_uses_exponential_backoff_so_retry_count_is_bounded()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        var callTimestamps = new System.Collections.Concurrent.ConcurrentQueue<long>();
        redis.Database
            .LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(_ => { callTimestamps.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp()); return false; });

        var sut = NewLock(redis);
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.AcquireAsync("k", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1500), token);
        lease.Should().BeSameAs(NoOpAsyncDisposable.Instance);

        var times = callTimestamps.ToArray();
        times.Length.Should().BeGreaterThanOrEqualTo(4,
            "exponential backoff with default 50ms initial and 1500ms timeout should fit several attempts before the deadline");

        // Inter-call gaps. With exponential growth, the longest gap should be substantially larger than
        // the first one — a fixed-interval regression (no backoff) would keep all gaps roughly equal.
        var gaps = Enumerable.Range(0, times.Length - 1)
            .Select(i => System.Diagnostics.Stopwatch.GetElapsedTime(times[i], times[i + 1]).TotalMilliseconds)
            .ToArray();
        var firstGap = gaps[0];
        var maxGap = gaps.Max();
        maxGap.Should().BeGreaterThan(firstGap * 2,
            $"exponential backoff should grow inter-call gaps; got firstGap={firstGap:F1}ms, maxGap={maxGap:F1}ms — " +
            "a regression to a fixed retry interval would keep gaps roughly constant");
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
