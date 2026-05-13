using StackExchange.Redis;
using UiPath.Platform.Caching.Locking;
using UiPath.Platform.Caching.Redis;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Tests.Locking;

public class RedisDistributedLockTests(ITestContextAccessor testContextAccessor)
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private RedisDistributedLock NewLock(IRedisConnector? redis = null, CacheOptions? options = null)
    {
        redis ??= _fixture.Freeze<IRedisConnector>();
        var opts = Options.Create(options ?? new CacheOptions());
        var telemetry = Substitute.For<ICachingTelemetryProvider>();
        return new RedisDistributedLock(redis, opts, telemetry);
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
        redis.Database
            .LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(false);

        var sut = NewLock(redis);
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.AcquireAsync("k", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1500), token);
        lease.Should().BeSameAs(NoOpAsyncDisposable.Instance);

        var calls = redis.Database.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IDatabaseAsync.LockTakeAsync));
        calls.Should().BeLessThan(15,
            "exponential backoff should bound retry count; a regressed fixed 50ms interval would call LockTakeAsync ~30 times in 1500ms");
        calls.Should().BeGreaterThan(2,
            "the retry loop should run at least a few times under sustained contention");
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
