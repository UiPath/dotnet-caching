using Microsoft.Extensions.Internal;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using UiPath.Caching;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

/// <summary>
/// Conditional add (<c>TryAddAsync</c>) on the Redis tier. The contract under test is narrow: one
/// <c>SET … NX</c> command, the Redis reply is the answer, and every non-win — lost race,
/// disconnected, thrown, unrepresentable value — reports <c>false</c> without a second round-trip.
/// </summary>
public class RedisCacheTryAddTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private ISystemClock _clock = default!;
    private RedisCacheOptions _cacheOptions = default!;
    private IDatabase _database = default!;
    private SystemJsonByteSerializerProxy _serializer = default!;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private CacheKey _cacheKey = default!;
    private RedisKey _redisKey = default!;
    private IRedisConnector _connector = default!;
    private IResiliencePipelineProvider _pipelineProvider = default!;
    private bool _isConnected = true;
    private readonly RecordingTelemetryProvider _telemetry = new();
    private RedisCache? _sut;

    private RedisCache Sut => _sut ??= _fixture.Create<RedisCache>();

    [Fact]
    public async Task TryAdd_issues_a_single_NX_write_and_reports_the_win()
    {
        var value = _fixture.Create<string>();
        _database.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, CommandFlags.DemandMaster)
            .Returns(true);

        var added = await Sut.TryAddAsync(_cacheKey, value, policy: null, token: testContextAccessor.Current.CancellationToken);

        added.Should().BeTrue();
        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, CommandFlags.DemandMaster);
        await _database.DidNotReceive().StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await _database.DidNotReceive().KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task TryAdd_never_writes_unconditionally()
    {
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(true);

        await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: testContextAccessor.Current.CancellationToken);

        await _database.DidNotReceive().StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.Always, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.Exists, Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task TryAdd_reports_not_added_when_the_key_already_exists()
    {
        _database.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, CommandFlags.DemandMaster)
            .Returns(false);

        var added = await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: testContextAccessor.Current.CancellationToken);

        added.Should().BeFalse();
    }

    [Fact]
    public async Task TryAdd_writes_a_payload_a_reader_can_deserialize()
    {
        var value = _fixture.Create<string>();
        RedisValue captured = default;
        _database.StringSetAsync(_redisKey, Arg.Do<RedisValue>(v => captured = v), Arg.Any<TimeSpan?>(), When.NotExists, Arg.Any<CommandFlags>())
            .Returns(true);

        await Sut.TryAddAsync(_cacheKey, value, policy: null, token: testContextAccessor.Current.CancellationToken);

        _serializer.Deserialize<string>(captured).Should().Be(value);
    }

    [Fact]
    public async Task TryAdd_fails_closed_when_disconnected()
    {
        _isConnected = false;
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(true);

        var added = await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: testContextAccessor.Current.CancellationToken);

        added.Should().BeFalse("a win that was never written would let a second caller win the same key");
        await _database.DidNotReceive().StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task TryAdd_fails_closed_when_the_write_throws()
    {
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));

        var added = await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: testContextAccessor.Current.CancellationToken);

        added.Should().BeFalse();
    }

    [Fact]
    public async Task TryAdd_applies_the_caller_expiration_in_the_same_command()
    {
        var ttl = TimeSpan.FromMinutes(3);
        _database.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, CommandFlags.DemandMaster)
            .Returns(true);

        await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), ttl, token: testContextAccessor.Current.CancellationToken);

        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), ttl, When.NotExists, CommandFlags.DemandMaster);
        await _database.DidNotReceive().KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task TryAdd_falls_back_to_the_policy_expiration()
    {
        var policyTtl = TimeSpan.FromMinutes(7);
        _database.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, CommandFlags.DemandMaster)
            .Returns(true);

        await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: new CachePolicy { DistributedExpiration = policyTtl }, token: testContextAccessor.Current.CancellationToken);

        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), policyTtl, When.NotExists, CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task TryAdd_accepts_an_absolute_expiration()
    {
        var absolute = _now.AddMinutes(4);
        _database.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, CommandFlags.DemandMaster)
            .Returns(true);

        await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), absolute, token: testContextAccessor.Current.CancellationToken);

        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), TimeSpan.FromMinutes(4), When.NotExists, CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task TryAdd_of_a_default_value_never_deletes_the_key()
    {
        _cacheOptions.CacheNullValues = false;

        var added = await Sut.TryAddAsync<string>(_cacheKey, null, TimeSpan.FromMinutes(1), token: testContextAccessor.Current.CancellationToken);

        added.Should().BeFalse("with no cached-null sentinel available there is nothing to claim the key with");
        await _database.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await _database.DidNotReceive().StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task TryAdd_of_a_default_value_claims_the_key_with_the_sentinel_when_CacheNullValues_is_on()
    {
        _cacheOptions.CacheNullValues = true;
        _sut = null;
        RedisValue captured = _fixture.Create<string>();
        _database.StringSetAsync(_redisKey, Arg.Do<RedisValue>(v => captured = v), Arg.Any<TimeSpan?>(), When.NotExists, CommandFlags.DemandMaster)
            .Returns(true);

        var added = await Sut.TryAddAsync<string>(_cacheKey, null, TimeSpan.FromMinutes(1), token: testContextAccessor.Current.CancellationToken);

        added.Should().BeTrue();
        captured.Length().Should().Be(0, "the empty string is the cached-null sentinel on the wire");
    }

    [Theory]
    [InlineData(true, "Caching.Stats.Hits.Redis.TryAddAsync.String")]
    [InlineData(false, "Caching.Stats.Misses.Redis.TryAddAsync.String")]
    public async Task TryAdd_reports_the_outcome_under_its_own_metric_scope(bool redisAdded, string expectedMetric)
    {
        _database.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, CommandFlags.DemandMaster)
            .Returns(redisAdded);

        await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: testContextAccessor.Current.CancellationToken);

        _telemetry.Metrics.Should().ContainSingle(m => m.Name == expectedMetric);
    }

    [Fact]
    public async Task TryAdd_honors_a_cancelled_token_before_touching_redis()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _database.DidNotReceive().StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task TryAdd_runs_the_NX_write_through_the_shared_write_pipeline()
    {
        var write = new CountingResiliencePipeline();
        _pipelineProvider.Get(ResiliencePipelineNames.Write).Returns(write);
        _database.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, CommandFlags.DemandMaster)
            .Returns(true);

        var added = await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: testContextAccessor.Current.CancellationToken);

        added.Should().BeTrue();
        write.Executions.Should().Be(1);
    }

    [Fact]
    public async Task TryAdd_surfaces_a_cancellation_raised_while_the_write_is_in_flight()
    {
        using var cts = new CancellationTokenSource();
        _database.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, CommandFlags.DemandMaster)
            .Returns<Task<bool>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var act = async () => await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_redis_failure_that_is_not_a_cancellation_still_reports_not_added()
    {
        _database.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, CommandFlags.DemandMaster)
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var added = await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: testContextAccessor.Current.CancellationToken);

        added.Should().BeFalse();
    }

    /// <summary>
    /// A non-positive lifetime used to return false, which is the same answer as "the key already
    /// exists". With expiration non-nullable there is no third state to lean on, so the argument is
    /// rejected rather than answered.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task TryAdd_rejects_a_non_positive_expiration(int minutes)
    {
        var act = async () => await Sut.TryAddAsync(
            _cacheKey,
            _fixture.Create<string>(),
            TimeSpan.FromMinutes(minutes),
            token: testContextAccessor.Current.CancellationToken);

        (await act.Should().ThrowAsync<ArgumentOutOfRangeException>()).And.ParamName.Should().Be("expiration");
        await _database.DidNotReceive().StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    public ValueTask InitializeAsync()
    {
        const string prefix = "test";
        _cacheKey = _fixture.Create<string>();
        _redisKey = string.Join(':', prefix, RedisTypePrefixes.String, _cacheKey).ToLowerInvariant();
        _clock = _fixture.Freeze<ISystemClock>();
        _clock.UtcNow.Returns(_ => _now);
        _pipelineProvider = _fixture.Freeze<IResiliencePipelineProvider>();
        var noOpExecutor = new EmptyResiliencePipeline();
        _pipelineProvider.Get(ResiliencePipelineNames.Read).Returns(noOpExecutor);
        _pipelineProvider.Get(ResiliencePipelineNames.Write).Returns(noOpExecutor);
        var cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        cacheKeyStrategy.GetCacheKey<string>(_cacheKey).Returns(_cacheKey);
        var redisKeyStrategyFactory = _fixture.Create<IRedisKeyStrategyFactory>();
        var redisKeyStrategy = _fixture.Create<IRedisKeyStrategy>();
        redisKeyStrategy.GetRedisKey(_cacheKey).Returns(_redisKey);
        redisKeyStrategyFactory.Create(Arg.Any<CacheOptions>(), Arg.Any<Type>()).Returns(redisKeyStrategy);
        _cacheOptions = new RedisCacheOptions
        {
            Clock = _clock,
            CacheKeyStrategy = cacheKeyStrategy,
            RedisKeyStrategyFactory = redisKeyStrategyFactory,
        };

        _database = _fixture.Freeze<IDatabase>();
        _serializer = new SystemJsonByteSerializerProxy();
        _fixture.Inject<ISerializerProxy<byte[]>>(_serializer);
        _fixture.Inject(Options.Create(_cacheOptions));
        _fixture.Inject(_cacheOptions);
        _fixture.Inject<ICachingTelemetryProvider>(_telemetry);
        _fixture.Inject(new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = true });
        _connector = _fixture.Freeze<IRedisConnector>();
        _connector.Database.Returns(_ => _database);
        _connector.Version.Returns(_ => new Version(6, 0));
        _connector.IsConnected.Returns(_ => _isConnected);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _sut?.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Executes the callback once and counts it, so a test can say which named pipeline a command was
    /// routed through without reaching into Polly.
    /// </summary>
    private sealed class CountingResiliencePipeline : IResiliencePipeline
    {
        private int _executions;

        public int Executions => Volatile.Read(ref _executions);

        public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executions);
            return callback(cancellationToken);
        }
    }
}
