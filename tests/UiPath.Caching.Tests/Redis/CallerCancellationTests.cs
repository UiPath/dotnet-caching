#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Locking;
using UiPath.Caching.Policies;
using UiPath.Caching.Polly;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

/// <summary>A call whose token is cancelled reaches its caller: no warning, no failure and no hit or miss is recorded for it.</summary>
public class CallerCancellationTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private readonly RecordingTelemetryProvider _telemetry = new();
    private CacheKey _cacheKey = default!;
    private IDatabase _database = default!;
    private ITransaction _transaction = default!;
    private ILogger<RedisCache> _logger = default!;
    private IResiliencePipelineProvider _pipelines = default!;

    private RedisCache Sut => _fixture.Create<RedisCache>();

    public ValueTask InitializeAsync()
    {
        const string prefix = "test";
        _cacheKey = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', prefix, RedisKeyspaces.String, _cacheKey).ToLowerInvariant();
        var clock = _fixture.Freeze<ISystemClock>();
        _fixture.Inject<TimeProvider>(new SystemClockTimeProvider(clock));
        clock.UtcNow.Returns(_ => DateTimeOffset.UtcNow);
        _pipelines = _fixture.Freeze<IResiliencePipelineProvider>();
        var noOpExecutor = new EmptyResiliencePipeline();
        _pipelines.Get(ResiliencePipelineNames.Read).Returns(noOpExecutor);
        _pipelines.Get(ResiliencePipelineNames.Write).Returns(noOpExecutor);
        var cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        cacheKeyStrategy.GetCacheKey<string>(_cacheKey).Returns(_cacheKey);
        var redisKeyStrategyFactory = _fixture.Create<IRedisKeyStrategyFactory>();
        var redisKeyStrategy = _fixture.Create<IRedisKeyStrategy>();
        redisKeyStrategy.GetRedisKey(_cacheKey).Returns(redisKey);
        redisKeyStrategyFactory.Create(Arg.Any<CacheOptions>(), Arg.Any<Type>()).Returns(redisKeyStrategy);
        var cacheOptions = new RedisCacheOptions
        {
            CacheKeyStrategy = cacheKeyStrategy,
            RedisKeyStrategyFactory = redisKeyStrategyFactory,
        };
        _database = _fixture.Freeze<IDatabase>();
        _transaction = Substitute.For<ITransaction>();
        _database.CreateTransaction(Arg.Any<object?>()).Returns(_transaction);
        _fixture.Inject<ISerializerProxy<byte[]>>(new SystemJsonByteSerializerProxy());
        _fixture.Inject(Options.Create(cacheOptions));
        _fixture.Inject(cacheOptions);
        _fixture.Inject<ICachingTelemetryProvider>(_telemetry);
        _fixture.Inject(new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = true });
        var connector = _fixture.Freeze<IRedisConnector>();
        connector.Database.Returns(_ => _database);
        connector.Version.Returns(_ => new Version(6, 0));
        connector.IsConnected.Returns(_ => true);
        _logger = _fixture.Freeze<ILogger<RedisCache>>();
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_read_the_caller_cancels_reaches_the_caller()
    {
        var pending = Pending<RedisValue>();
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(pending.Task);

        await CancelMidFlightAsync(pending, token => Sut.GetAsync<string>(_cacheKey, policy: null, token: token).AsTask());
    }

    [Fact]
    public async Task A_read_of_several_keys_the_caller_cancels_reaches_the_caller()
    {
        var pending = Pending<RedisValue[]>();
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(pending.Task);

        await CancelMidFlightAsync(pending, token => Sut.GetAsync<string>([_cacheKey], policy: null, token: token).AsTask());
    }

    [Fact]
    public async Task A_get_or_add_probe_the_caller_cancels_reaches_the_caller_and_never_generates()
    {
        // Its continuations run inline, so the shared run has ended, telemetry included, when the fault is set.
        var pending = new TaskCompletionSource<RedisValue>();
        var generated = false;
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(pending.Task);

        Task<string?> Generate(CancellationToken ct)
        {
            generated = true;
            return Task.FromResult<string?>("v");
        }

        await CancelMidFlightAsync(pending, token => Sut.GetOrAddAsync<string>(_cacheKey, Generate, policy: null, token: token).AsTask());

        NoFailureRecorded();
        generated.Should().BeFalse();
    }

    [Fact]
    public async Task A_remove_the_caller_cancels_reaches_the_caller()
    {
        var pending = Pending<bool>();
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(pending.Task);

        await CancelMidFlightAsync(pending, token => Sut.RemoveAsync<string>(_cacheKey, token).AsTask());
    }

    [Fact]
    public async Task A_remove_of_several_keys_the_caller_cancels_reaches_the_caller()
    {
        var pending = Pending<long>();
        _database.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(pending.Task);

        await CancelMidFlightAsync(pending, token => Sut.RemoveAsync<string>([_cacheKey], token).AsTask());
    }

    [Fact]
    public async Task A_write_the_caller_cancels_reaches_the_caller()
    {
        var pending = Pending<bool>();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(pending.Task);

        await CancelMidFlightAsync(pending, token => Sut.SetAsync(_cacheKey, "v", policy: null, token: token).AsTask());
    }

    [Fact]
    public async Task A_write_of_several_keys_the_caller_cancels_reaches_the_caller()
    {
        var pending = Pending<bool>();
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(pending.Task);

        await CancelMidFlightAsync(pending, token => Sut.SetAsync<string>([new(_cacheKey, "v")], TimeSpan.FromMinutes(1), policy: null, token).AsTask());
    }

    [Fact]
    public async Task A_conditional_add_the_caller_cancels_reaches_the_caller()
    {
        var pending = Pending<bool>();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, Arg.Any<CommandFlags>()).Returns(pending.Task);

        await CancelMidFlightAsync(pending, token => Sut.TryAddAsync(_cacheKey, "v", policy: null, token: token).AsTask());
    }

    [Fact]
    public async Task A_refresh_the_caller_cancels_reaches_the_caller()
    {
        var pending = Pending<bool>();
        _database.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>()).Returns(pending.Task);

        await CancelMidFlightAsync(pending, token => Sut.RefreshAsync<string>(_cacheKey, TimeSpan.FromMinutes(1), policy: null, token).AsTask());
    }

    [Fact]
    public async Task A_contains_the_caller_cancels_reaches_the_caller()
    {
        var pending = Pending<bool>();
        _database.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(pending.Task);

        await CancelMidFlightAsync(pending, token => Sut.ContainsAsync<string>(_cacheKey, token).AsTask());
    }

    [Fact]
    public async Task A_time_to_live_the_caller_cancels_is_still_answered_as_a_miss_without_a_warning()
    {
        using var cts = new CancellationTokenSource();
        var pending = Pending<TimeSpan?>();
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(pending.Task);

        var answering = Sut.TimeToLiveAsync<string>(_cacheKey, cts.Token).AsTask();
        await cts.CancelAsync();
        pending.SetException(new OperationCanceledException(cts.Token));

        (await answering).Should().BeNull();
        WarningsLogged().Should().Be(0);
    }

    [Fact]
    public async Task An_expire_time_the_caller_cancels_is_still_answered_as_a_miss_without_a_warning()
    {
        using var cts = new CancellationTokenSource();
        var pending = Pending<TimeSpan?>();
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(pending.Task);

        var answering = Sut.ExpireTimeAsync<string>(_cacheKey, cts.Token).AsTask();
        await cts.CancelAsync();
        pending.SetException(new OperationCanceledException(cts.Token));

        (await answering).Should().BeNull();
        WarningsLogged().Should().Be(0);
    }

    [Fact]
    public async Task A_write_cancelled_by_a_timer_the_library_owns_reaches_its_owner()
    {
        using var timer = new CancellationTokenSource();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(_ =>
            {
                // The timer fires once the write is in flight, never before it reaches Redis.
                timer.Cancel();
                return HangUntilAsync(timer.Token);
            });

        var write = Sut.SetAsync(_cacheKey, "v", policy: null, token: timer.Token).AsTask();

        await write.Invoking(w => w).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_read_the_caller_cancels_through_the_real_resilience_pipeline_is_not_retried_or_logged()
    {
        UseRealPipelines();
        using var cts = new CancellationTokenSource();
        var reads = 0;
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(_ =>
        {
            Interlocked.Increment(ref reads);
            return new TaskCompletionSource<RedisValue>().Task;
        });

        var read = Sut.GetAsync<string>(_cacheKey, policy: null, token: cts.Token).AsTask();
        await cts.CancelAsync();

        await read.Invoking(r => r).Should().ThrowAsync<OperationCanceledException>();
        reads.Should().Be(1);
        NoFailureRecorded();
    }

    [Fact]
    public async Task A_write_the_caller_cancels_through_the_real_resilience_pipeline_is_not_retried_or_logged()
    {
        UseRealPipelines();
        using var cts = new CancellationTokenSource();
        var writes = 0;
        var pending = Pending<bool>();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(_ =>
        {
            Interlocked.Increment(ref writes);
            return pending.Task;
        });

        var write = Sut.SetAsync(_cacheKey, "v", policy: null, token: cts.Token).AsTask();
        await cts.CancelAsync();
        pending.SetException(new OperationCanceledException(cts.Token));

        await write.Invoking(w => w).Should().ThrowAsync<OperationCanceledException>();
        writes.Should().Be(1);
        NoFailureRecorded();
    }

    [Fact]
    public async Task A_lock_the_caller_cancels_is_not_reported_unavailable()
    {
        using var cts = new CancellationTokenSource();
        var redis = _fixture.Freeze<IRedisConnector>();
        var pending = Pending<bool>();
        redis.Database.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>()).Returns(pending.Task);
        var sut = new RedisDistributedLock(redis, Options.Create(new CacheOptions()), _telemetry);

        var acquiring = sut.TryAcquireAsync("k", TimeSpan.FromSeconds(5), cts.Token).AsTask();
        await cts.CancelAsync();
        pending.SetException(new OperationCanceledException(cts.Token));

        await acquiring.Invoking(a => a).Should().ThrowAsync<OperationCanceledException>();
        _telemetry.Exceptions.Should().BeEmpty();
        _telemetry.Events.Should().NotContain(e => e.Name == "cache.distributedlock.unavailable");
    }

    [Fact]
    public async Task A_lock_wait_the_caller_cancels_is_not_reported_unavailable()
    {
        using var cts = new CancellationTokenSource();
        var redis = _fixture.Freeze<IRedisConnector>();
        var pending = Pending<bool>();
        redis.Database.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>()).Returns(pending.Task);
        var sut = new RedisDistributedLock(redis, Options.Create(new CacheOptions()), _telemetry);

        var acquiring = sut.AcquireAsync("k", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), cts.Token).AsTask();
        await cts.CancelAsync();
        pending.SetException(new OperationCanceledException(cts.Token));

        await acquiring.Invoking(a => a).Should().ThrowAsync<OperationCanceledException>();
        _telemetry.Exceptions.Should().BeEmpty();
        _telemetry.Events.Should().NotContain(e => e.Name == "cache.distributedlock.unavailable");
    }

    [Fact]
    public async Task A_lock_acquire_that_the_factory_timeout_ends_is_still_reported_as_a_factory_timeout()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.ConnectAsync(Arg.Any<CancellationToken>()).Returns(call => new ValueTask(Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>())));
        var sut = new RedisDistributedLock(redis, Options.Create(new CacheOptions()), _telemetry);

        var acquiring = FactoryTimeout.RunAsync<IAsyncDisposable?>(
            ct => sut.TryAcquireAsync("k", TimeSpan.FromSeconds(5), ct).AsTask(),
            TimeSpan.FromMilliseconds(50),
            _cacheKey,
            "cache",
            _telemetry,
            CancellationToken.None,
            source: FactoryTimeout.SourceRehydrateLock);

        await acquiring.Invoking(a => a).Should().ThrowAsync<TimeoutException>();
        _telemetry.Events.Should().ContainSingle(e => e.Name == "cache.factory.timed_out").Which.Properties!["source"].Should().Be("rehydrate-lock");
    }

    [Fact]
    public async Task A_health_probe_the_host_cancels_is_rethrown_by_the_health_check_service()
    {
        using var cts = new CancellationTokenSource();
        using var provider = HealthServiceWith(HungConnector(), TimeSpan.FromSeconds(30));
        var checking = provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(cts.Token);
        await cts.CancelAsync();

        await checking.Invoking(c => c).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_health_probe_that_runs_past_the_services_timeout_is_reported_unhealthy()
    {
        using var provider = HealthServiceWith(HungConnector(), TimeSpan.FromMilliseconds(100));

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(testContextAccessor.Current.CancellationToken);

        report.Status.Should().Be(HealthStatus.Unhealthy);
    }

    private static async Task<bool> HangUntilAsync(CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        return true;
    }

    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ServiceProvider HealthServiceWith(IRedisConnector connector, TimeSpan timeout) =>
        new ServiceCollection()
            .AddLogging()
            .AddHealthChecks()
            .Add(new HealthCheckRegistration("redis", _ => new RedisHealthCheck(connector), HealthStatus.Unhealthy, null, timeout))
            .Services
            .BuildServiceProvider();

    private static IRedisConnector HungConnector()
    {
        var connector = Substitute.For<IRedisConnector>();
        connector.ConnectAsync(Arg.Any<CancellationToken>()).Returns(call => new ValueTask(Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>())));
        return connector;
    }

    private void UseRealPipelines()
    {
        var options = Substitute.For<IOptionsMonitor<ResiliencePoliciesOptions>>();
        var policies = new ResiliencePoliciesOptions { TelemetryEnabled = false };
        options.CurrentValue.Returns(policies);
        options.Get(Arg.Any<string?>()).Returns(policies);
        var factory = new ResiliencePipelineFactory(NullLoggerFactory.Instance, null, options);
        _pipelines.Get(ResiliencePipelineNames.Read).Returns(new ResiliencePipelineWrapper(factory, ResiliencePipelineNames.Read));
        _pipelines.Get(ResiliencePipelineNames.Write).Returns(new ResiliencePipelineWrapper(factory, ResiliencePipelineNames.Write));
    }

    private async Task CancelMidFlightAsync<T>(TaskCompletionSource<T> pending, Func<CancellationToken, Task> call)
    {
        using var cts = new CancellationTokenSource();
        var running = call(cts.Token);
        await cts.CancelAsync();
        pending.TrySetException(new OperationCanceledException(cts.Token));

        await running.Invoking(r => r).Should().ThrowAsync<OperationCanceledException>();
        NoFailureRecorded();
    }

    private int WarningsLogged() =>
        _logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ILogger.Log) && (LogLevel)c.GetArguments()[0]! >= LogLevel.Warning);

    private void NoFailureRecorded()
    {
        WarningsLogged().Should().Be(0);
        _telemetry.Exceptions.Should().BeEmpty();
        _telemetry.Metrics.Should().NotContain(m => m.Name.StartsWith("Caching.Stats.", StringComparison.Ordinal), "a cancelled call is neither a hit nor a miss");
    }
}
