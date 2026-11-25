using Microsoft.Extensions.Internal;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using UiPath.Caching;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

/// <summary>A batch fill answers for each key on its own: a command that failed fails only its key.</summary>
public class RedisCacheFillManyTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private readonly DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly IBatch _batch = Substitute.For<IBatch>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();
    private RedisKey _redisFirst;
    private RedisKey _redisSecond;
    private IDatabase _database = default!;
    private RedisCache? _sut;

    private RedisCache Sut => _sut ??= _fixture.Create<RedisCache>();

    [Fact]
    public async Task A_command_that_failed_leaves_the_other_keys_of_its_slot_their_outcomes()
    {
        Sets(_redisFirst, Task.FromResult(true));
        Sets(_redisSecond, Task.FromException<bool>(new RedisException("down")));

        var results = await FillAsync();

        results[0].Outcome.Should().Be(FillOutcome.Added, "its command won, whatever became of the other key's");
        results[1].Outcome.Should().Be(FillOutcome.Failed);
    }

    [Fact]
    public async Task A_winner_that_cannot_be_read_fails_only_its_key()
    {
        Sets(_redisFirst, Task.FromResult(true));
        Sets(_redisSecond, Task.FromResult(false));
        _transaction.StringGetAsync(_redisSecond, Arg.Any<CommandFlags>()).Returns(Task.FromException<RedisValue>(new RedisException("down")));

        var results = await FillAsync();

        results[0].Outcome.Should().Be(FillOutcome.Added);
        results[1].Outcome.Should().Be(FillOutcome.Failed);
    }

    [Fact]
    public async Task A_key_that_lost_its_command_adopts_the_stored_value_while_another_key_of_its_slot_failed()
    {
        Sets(_redisFirst, Task.FromException<bool>(new RedisException("down")));
        Sets(_redisSecond, Task.FromResult(false));
        _transaction.StringGetAsync(_redisSecond, Arg.Any<CommandFlags>()).Returns(Task.FromResult<RedisValue>("\"stored\""u8.ToArray()));

        var results = await FillAsync();

        results[0].Outcome.Should().Be(FillOutcome.Failed);
        results[1].Outcome.Should().Be(FillOutcome.Existed);
        results[1].Existing.Should().Be("stored");
    }

    public ValueTask InitializeAsync()
    {
        const string prefix = "test";
        CacheKey first = "first";
        CacheKey second = "second";
        _redisFirst = string.Join(':', prefix, RedisKeyspaces.String, first.Name).ToLowerInvariant();
        _redisSecond = string.Join(':', prefix, RedisKeyspaces.String, second.Name).ToLowerInvariant();
        var clock = _fixture.Freeze<ISystemClock>();
        _fixture.Inject<TimeProvider>(new SystemClockTimeProvider(clock));
        clock.UtcNow.Returns(_ => _now);
        var pipelines = _fixture.Freeze<IResiliencePipelineProvider>();
        var noOpExecutor = new EmptyResiliencePipeline();
        pipelines.Get(ResiliencePipelineNames.Read).Returns(noOpExecutor);
        pipelines.Get(ResiliencePipelineNames.Write).Returns(noOpExecutor);
        var cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        cacheKeyStrategy.GetCacheKey<string>(first.Name).Returns(first);
        cacheKeyStrategy.GetCacheKey<string>(second.Name).Returns(second);
        var redisKeyStrategyFactory = _fixture.Create<IRedisKeyStrategyFactory>();
        var redisKeyStrategy = _fixture.Create<IRedisKeyStrategy>();
        redisKeyStrategy.GetRedisKey(first).Returns(_redisFirst);
        redisKeyStrategy.GetRedisKey(second).Returns(_redisSecond);
        redisKeyStrategyFactory.Create(Arg.Any<CacheOptions>(), Arg.Any<Type>()).Returns(redisKeyStrategy);
        var options = new RedisCacheOptions
        {
            CacheKeyStrategy = cacheKeyStrategy,
            RedisKeyStrategyFactory = redisKeyStrategyFactory,
            ReadScriptsEnabled = false,
        };

        _database = _fixture.Freeze<IDatabase>();
        _database.CreateBatch(Arg.Any<object>()).Returns(_batch);
        _database.CreateTransaction(Arg.Any<object>()).Returns(_transaction);
        _database.Multiplexer.GetHashSlot(Arg.Any<RedisKey>()).Returns(7);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult<TimeSpan?>(TimeSpan.FromMinutes(1)));
        _fixture.Inject<ISerializerProxy<byte[]>>(new SystemJsonByteSerializerProxy());
        _fixture.Inject(Options.Create(options));
        _fixture.Inject(options);
        _fixture.Inject<ICachingTelemetryProvider>(_telemetry);
        _fixture.Inject(new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = true });
        var connector = _fixture.Freeze<IRedisConnector>();
        connector.Database.Returns(_ => _database);
        connector.Version.Returns(_ => new Version(6, 0));
        connector.IsConnected.Returns(_ => true);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _sut?.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void Sets(RedisKey key, Task<bool> answer) =>
        _batch.StringSetAsync(key, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, CommandFlags.DemandMaster).Returns(answer);

    private async Task<FillResult<string>[]> FillAsync() =>
        await Sut.TryFillManyAsync<string>([new("first", "a"), new("second", "b")], _now.AddMinutes(5), policy: null, testContextAccessor.Current.CancellationToken);
}
