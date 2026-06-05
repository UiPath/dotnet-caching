using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Tests.Redis;

public class RedisSetCacheTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private string _prefix = default!;
    private IDatabase _database = default!;
    private ITransaction _transaction = default!;
    private ISerializerProxy<RedisValue> _serializer = default!;
    private ISystemClock _clock = default!;
    private const string PopResilienceKeyName = "set-pop";
    private RedisCacheOptions _redisCacheOptions = new();
    private readonly RedisSetCacheOptions _setCacheOptions = new() { ResilienceKeyName = PopResilienceKeyName };
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private IResiliencePipelineProvider _pipelineProvider = default!;
    private CacheKey _cacheKey = default!;
    private RedisKey _redisKey = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private IRedisKeyStrategyFactory _redisKeyStrategyFactory = default!;
    private IRedisKeyStrategy _redisKeyStrategy = default!;
    private IRedisConnector _connector = default!;
    private bool _isConnected = true;
    private ILogger<RedisSetCache> _logger = default!;
    private RedisSetCache? _sut;

    private RedisSetCache Sut => _sut ??= _fixture.Create<RedisSetCache>();

    [Fact]
    public void Name_is_Redis() => Sut.Name.Should().Be("Redis");

    [Fact]
    public void Uses_se_key_prefix()
    {
        _ = Sut;
        _redisKeyStrategyFactory.Received(1).Create(Arg.Any<CacheOptions>(), "se");
    }

    [Fact]
    public async Task Add_single_works()
    {
        var item = _fixture.Create<TestDto>();
        _transaction.SetAddAsync(_redisKey, Arg.Any<RedisValue[]>(), CommandFlags.DemandMaster).Returns(1L);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var actual = await Sut.AddAsync(_cacheKey, item, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeTrue();
        _database.Received(1).CreateTransaction();
        await _transaction.Received(1).SetAddAsync(_redisKey, Arg.Any<RedisValue[]>(), CommandFlags.DemandMaster);
        await _transaction.Received(1).KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task Add_single_transaction_fail_returns_false()
    {
        var item = _fixture.Create<TestDto>();
        _transaction.SetAddAsync(_redisKey, Arg.Any<RedisValue[]>(), CommandFlags.DemandMaster).Returns(1L);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);

        var actual = await Sut.AddAsync(_cacheKey, item, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Add_single_not_connected_returns_false()
    {
        _redisCacheOptions.ConnectionMonitorEnabled = true;
        _isConnected = false;
        var item = _fixture.Create<TestDto>();

        var actual = await Sut.AddAsync(_cacheKey, item, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeFalse();
        _database.DidNotReceive().CreateTransaction();
    }

    [Fact]
    public async Task Add_single_redis_exception_returns_false()
    {
        var item = _fixture.Create<TestDto>();
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).ThrowsAsync<Exception>();

        var actual = await Sut.AddAsync(_cacheKey, item, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeFalse();
        _logger.ReceivedCalls().Should().Contain(c => c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning);
    }

    [Fact]
    public async Task Add_many_works()
    {
        var items = _fixture.CreateMany<TestDto>().ToArray();
        _transaction.SetAddAsync(_redisKey, Arg.Any<RedisValue[]>(), CommandFlags.DemandMaster).Returns(items.Length);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var actual = await Sut.AddAsync<TestDto>(_cacheKey, items, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().Be(items.Length);
        _database.Received(1).CreateTransaction();
        await _transaction.Received(1).SetAddAsync(_redisKey, Arg.Any<RedisValue[]>(), CommandFlags.DemandMaster);
        await _transaction.Received(1).KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task Add_many_with_expiration_works()
    {
        var items = _fixture.CreateMany<TestDto>().ToArray();
        _transaction.SetAddAsync(_redisKey, Arg.Any<RedisValue[]>(), CommandFlags.DemandMaster).Returns(items.Length);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var actual = await Sut.AddAsync<TestDto>(_cacheKey, items, _clock.UtcNow.AddHours(1), policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().Be(items.Length);
        await _transaction.Received(1).KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task Add_many_empty_returns_zero_without_transaction()
    {
        var actual = await Sut.AddAsync<TestDto>(_cacheKey, Array.Empty<TestDto>(), policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().Be(0);
        _database.DidNotReceive().CreateTransaction();
    }

    [Fact]
    public async Task Add_many_null_items_throws()
    {
        Func<Task> act = async () => await Sut.AddAsync<TestDto>(_cacheKey, items: null!, policy: null, token: testContextAccessor.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Add_many_transaction_fail_returns_zero()
    {
        var items = _fixture.CreateMany<TestDto>().ToArray();
        _transaction.SetAddAsync(_redisKey, Arg.Any<RedisValue[]>(), CommandFlags.DemandMaster).Returns(items.Length);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);

        var actual = await Sut.AddAsync<TestDto>(_cacheKey, items, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().Be(0);
    }

    [Fact]
    public async Task Add_many_redis_exception_returns_zero()
    {
        var items = _fixture.CreateMany<TestDto>().ToArray();
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).ThrowsAsync<Exception>();

        var actual = await Sut.AddAsync<TestDto>(_cacheKey, items, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().Be(0);
    }

    [Fact]
    public async Task Pop_single_works()
    {
        var expected = _fixture.Create<TestDto>();
        _database.SetPopAsync(_redisKey, CommandFlags.DemandMaster).Returns(_serializer.Serialize(expected));

        var actual = await Sut.PopAsync<TestDto>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Pop_single_empty_returns_default()
    {
        _database.SetPopAsync(_redisKey, CommandFlags.DemandMaster).Returns(RedisValue.Null);

        var actual = await Sut.PopAsync<TestDto>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeNull();
    }

    [Fact]
    public async Task Pop_single_redis_exception_returns_default()
    {
        _database.SetPopAsync(_redisKey, CommandFlags.DemandMaster).ThrowsAsync<Exception>();

        var actual = await Sut.PopAsync<TestDto>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeNull();
    }

    [Fact]
    public async Task Pop_count_works()
    {
        var expected = _fixture.CreateMany<TestDto>().ToArray();
        var serialized = expected.Select(e => _serializer.Serialize(e)).ToArray();
        _database.SetPopAsync(_redisKey, expected.Length, CommandFlags.DemandMaster).Returns(serialized);

        var actual = await Sut.PopAsync<TestDto>(_cacheKey, expected.Length, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Pop_count_non_positive_returns_empty_without_redis_call(long count)
    {
        var actual = await Sut.PopAsync<TestDto>(_cacheKey, count, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeEmpty();
        await _database.DidNotReceive().SetPopAsync(_redisKey, Arg.Any<long>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Pop_count_redis_exception_returns_empty()
    {
        _database.SetPopAsync(_redisKey, Arg.Any<long>(), CommandFlags.DemandMaster).ThrowsAsync<Exception>();

        var actual = await Sut.PopAsync<TestDto>(_cacheKey, 3, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task Pop_single_not_connected_returns_default_without_redis_call()
    {
        _redisCacheOptions.ConnectionMonitorEnabled = true;
        _isConnected = false;

        var actual = await Sut.PopAsync<TestDto>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeNull();
        await _database.DidNotReceive().SetPopAsync(_redisKey, Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Pop_count_not_connected_returns_empty_without_redis_call()
    {
        _redisCacheOptions.ConnectionMonitorEnabled = true;
        _isConnected = false;

        var actual = await Sut.PopAsync<TestDto>(_cacheKey, 3, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeEmpty();
        await _database.DidNotReceive().SetPopAsync(_redisKey, Arg.Any<long>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Pop_single_uses_configured_pop_pipeline()
    {
        var pop = new CountingPipeline();
        var write = new CountingPipeline();
        _pipelineProvider.Get(PopResilienceKeyName).Returns(pop);
        _pipelineProvider.Get(ResiliencePipelineNames.Write).Returns(write);
        var expected = _fixture.Create<TestDto>();
        _database.SetPopAsync(_redisKey, CommandFlags.DemandMaster).Returns(_serializer.Serialize(expected));

        var actual = await Sut.PopAsync<TestDto>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeEquivalentTo(expected);
        pop.Calls.Should().Be(1);
        write.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Pop_count_uses_configured_pop_pipeline()
    {
        var pop = new CountingPipeline();
        var write = new CountingPipeline();
        _pipelineProvider.Get(PopResilienceKeyName).Returns(pop);
        _pipelineProvider.Get(ResiliencePipelineNames.Write).Returns(write);
        var expected = _fixture.CreateMany<TestDto>().ToArray();
        var serialized = expected.Select(e => _serializer.Serialize(e)).ToArray();
        _database.SetPopAsync(_redisKey, expected.Length, CommandFlags.DemandMaster).Returns(serialized);

        var actual = await Sut.PopAsync<TestDto>(_cacheKey, expected.Length, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeEquivalentTo(expected);
        pop.Calls.Should().Be(1);
        write.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RemoveItem_uses_write_pipeline_not_pop()
    {
        var pop = new CountingPipeline();
        var write = new CountingPipeline();
        _pipelineProvider.Get(PopResilienceKeyName).Returns(pop);
        _pipelineProvider.Get(ResiliencePipelineNames.Write).Returns(write);
        var item = _fixture.Create<TestDto>();
        _database.SetRemoveAsync(_redisKey, Arg.Any<RedisValue>(), CommandFlags.DemandMaster).Returns(true);

        var actual = await Sut.RemoveItemAsync(_cacheKey, item, testContextAccessor.Current.CancellationToken);

        actual.Should().BeTrue();
        write.Calls.Should().Be(1);
        pop.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Members_works()
    {
        var expected = _fixture.CreateMany<TestDto>().ToArray();
        var serialized = expected.Select(e => _serializer.Serialize(e)).ToArray();
        _database.SetMembersAsync(_redisKey, CommandFlags.PreferReplica).Returns(serialized);

        var actual = await Sut.MembersAsync<TestDto>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Members_not_connected_returns_empty()
    {
        _redisCacheOptions.ConnectionMonitorEnabled = true;
        _isConnected = false;

        var actual = await Sut.MembersAsync<TestDto>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeEmpty();
        await _database.DidNotReceive().SetMembersAsync(_redisKey, Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Members_redis_exception_returns_empty()
    {
        _database.SetMembersAsync(_redisKey, CommandFlags.PreferReplica).ThrowsAsync<Exception>();

        var actual = await Sut.MembersAsync<TestDto>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task ContainsItem_works()
    {
        var item = _fixture.Create<TestDto>();
        _database.SetContainsAsync(_redisKey, Arg.Any<RedisValue>(), CommandFlags.PreferReplica).Returns(true);

        var actual = await Sut.ContainsItemAsync(_cacheKey, item, testContextAccessor.Current.CancellationToken);

        actual.Should().BeTrue();
    }

    [Fact]
    public async Task ContainsItem_redis_exception_returns_false()
    {
        var item = _fixture.Create<TestDto>();
        _database.SetContainsAsync(_redisKey, Arg.Any<RedisValue>(), CommandFlags.PreferReplica).ThrowsAsync<Exception>();

        var actual = await Sut.ContainsItemAsync(_cacheKey, item, testContextAccessor.Current.CancellationToken);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Count_works()
    {
        var expected = _fixture.Create<long>();
        _database.SetLengthAsync(_redisKey, CommandFlags.PreferReplica).Returns(expected);

        var actual = await Sut.CountAsync<TestDto>(_cacheKey, testContextAccessor.Current.CancellationToken);

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task Count_redis_exception_returns_zero()
    {
        _database.SetLengthAsync(_redisKey, CommandFlags.PreferReplica).ThrowsAsync<Exception>();

        var actual = await Sut.CountAsync<TestDto>(_cacheKey, testContextAccessor.Current.CancellationToken);

        actual.Should().Be(0);
    }

    [Fact]
    public async Task RemoveItem_works()
    {
        var item = _fixture.Create<TestDto>();
        _database.SetRemoveAsync(_redisKey, Arg.Any<RedisValue>(), CommandFlags.DemandMaster).Returns(true);

        var actual = await Sut.RemoveItemAsync(_cacheKey, item, testContextAccessor.Current.CancellationToken);

        actual.Should().BeTrue();
        await _database.Received(1).SetRemoveAsync(_redisKey, Arg.Any<RedisValue>(), CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task RemoveItem_redis_exception_returns_false()
    {
        var item = _fixture.Create<TestDto>();
        _database.SetRemoveAsync(_redisKey, Arg.Any<RedisValue>(), CommandFlags.DemandMaster).ThrowsAsync<Exception>();

        var actual = await Sut.RemoveItemAsync(_cacheKey, item, testContextAccessor.Current.CancellationToken);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveItems_works()
    {
        var items = _fixture.CreateMany<TestDto>().ToArray();
        _database.SetRemoveAsync(_redisKey, Arg.Any<RedisValue[]>(), CommandFlags.DemandMaster).Returns(items.Length);

        var actual = await Sut.RemoveItemsAsync(_cacheKey, items, testContextAccessor.Current.CancellationToken);

        actual.Should().Be(items.Length);
        await _database.Received(1).SetRemoveAsync(_redisKey, Arg.Any<RedisValue[]>(), CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task RemoveItems_empty_returns_zero_without_redis_call()
    {
        var actual = await Sut.RemoveItemsAsync(_cacheKey, Array.Empty<TestDto>(), testContextAccessor.Current.CancellationToken);

        actual.Should().Be(0);
        await _database.DidNotReceive().SetRemoveAsync(_redisKey, Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task RemoveItems_null_throws()
    {
        Func<Task> act = async () => await Sut.RemoveItemsAsync<TestDto>(_cacheKey, items: null!, testContextAccessor.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RemoveItems_redis_exception_returns_zero()
    {
        var items = _fixture.CreateMany<TestDto>().ToArray();
        _database.SetRemoveAsync(_redisKey, Arg.Any<RedisValue[]>(), CommandFlags.DemandMaster).ThrowsAsync<Exception>();

        var actual = await Sut.RemoveItemsAsync(_cacheKey, items, testContextAccessor.Current.CancellationToken);

        actual.Should().Be(0);
    }

    [Fact]
    public async Task Remove_works()
    {
        var expected = _fixture.Create<bool>();
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster).Returns(expected);

        var actual = await Sut.RemoveAsync<TestDto>(_cacheKey, testContextAccessor.Current.CancellationToken);

        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Remove_validates_cacheKey(string? cacheKey)
    {
        Func<Task> act = async () => await Sut.RemoveAsync<TestDto>(cacheKey);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Remove_redis_exception_returns_false()
    {
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster).ThrowsAsync<Exception>();

        var actual = await Sut.RemoveAsync<TestDto>(_cacheKey, testContextAccessor.Current.CancellationToken);

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Contains_works()
    {
        _database.KeyExistsAsync(_redisKey, CommandFlags.PreferReplica).Returns(true);

        var actual = await Sut.ContainsAsync<TestDto>(_cacheKey, testContextAccessor.Current.CancellationToken);

        actual.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_null_cacheKey_throws()
    {
        Func<Task> act = async () => await Sut.ContainsAsync<TestDto>(CacheKey.Null);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Contains_redis_exception_returns_false()
    {
        _database.KeyExistsAsync(_redisKey, CommandFlags.PreferReplica).ThrowsAsync<Exception>();

        var actual = await Sut.ContainsAsync<TestDto>(_cacheKey, testContextAccessor.Current.CancellationToken);

        actual.Should().BeFalse();
    }

    [Fact]
    public void Dispose_can_be_called()
    {
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    private sealed class CountingPipeline : IResiliencePipeline
    {
        public int Calls { get; private set; }

        public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
        {
            Calls++;
            return callback(cancellationToken);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask InitializeAsync()
    {
        _prefix = _fixture.Create<string>();
        _cacheKey = _fixture.Create<string>();
        _redisKey = string.Join(':', _prefix, "se", _cacheKey).ToLowerInvariant();

        _database = _fixture.Freeze<IDatabase>();
        _transaction = _fixture.Freeze<ITransaction>();
        _clock = _fixture.Freeze<ISystemClock>();
        _clock.UtcNow.Returns(_ => _now);
        _logger = _fixture.Freeze<ILogger<RedisSetCache>>();
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        _pipelineProvider = _fixture.Freeze<IResiliencePipelineProvider>();
        var resiliencePipeline = new EmptyResiliencePipeline();
        _pipelineProvider.Get(ResiliencePipelineNames.Read).Returns(resiliencePipeline);
        _pipelineProvider.Get(ResiliencePipelineNames.Write).Returns(resiliencePipeline);
        _pipelineProvider.Get(PopResilienceKeyName).Returns(resiliencePipeline);
        _fixture.Inject(_setCacheOptions);
        _database.CreateTransaction().Returns(_transaction);
        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        _redisKeyStrategyFactory = _fixture.Create<IRedisKeyStrategyFactory>();
        _redisKeyStrategy = _fixture.Create<IRedisKeyStrategy>();
        _redisKeyStrategy.GetRedisKey(_cacheKey).Returns(_redisKey);
        _redisKeyStrategyFactory.Create(Arg.Any<CacheOptions>(), Arg.Any<string>())
            .Returns(_redisKeyStrategy);

        _redisCacheOptions = new RedisCacheOptions
        {
            DefaultExpiration = TimeSpan.FromSeconds(Random.Shared.Next(1, 100)),
            Clock = _clock,
            CacheKeyStrategy = _cacheKeyStrategy,
            RedisKeyStrategyFactory = _redisKeyStrategyFactory
        };
        _serializer = new SystemJsonSerializerProxy();
        _fixture.Inject(_serializer);
        var opt = Options.Create(_redisCacheOptions);
        _fixture.Inject(opt);
        _fixture.Inject(opt.Value);
        _connector = _fixture.Freeze<IRedisConnector>();
        _connector.Database.Returns(_ => _database);
        _connector.IsConnected.Returns(_ => _isConnected);
        return ValueTask.CompletedTask;
    }
}
