using Microsoft.Extensions.Internal;
using NSubstitute.ExceptionExtensions;
using NSubstitute.ReceivedExtensions;
using StackExchange.Redis;
using UiPath.Platform.Caching;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Tests.Redis;

public class RedisCacheTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private ISystemClock _clock = default!;
    private IPolicyHolder _policyHolder = default!;
    private RedisCacheOptions _cacheOptions = default!;
    private IDatabase _database = default!;
    private ISerializerProxy _serializer = default!;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private CacheKey _cacheKey = default!;
    private RedisKey _redisKey = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private IRedisKeyStrategy _redisKeyStrategy = default!;
    private string _prefix = default!;
    private RedisCache? _sut = null;
    private RedisCache Sut => _sut ??= _fixture.Create<RedisCache>();

    [Fact]
    public async Task Get_works_as_expected()
    {
        var expectedValue = _fixture.Create<string>();
        _database.StringGetAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(ci =>
            {
                return _serializer.Serialize(expectedValue);
            });
        var actualValue = await Sut.GetAsync<string>(_cacheKey);
        actualValue.Should().Be(expectedValue);
    }

    [Fact]
    public async Task Get_has_no_redis_exceptions()
    {
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));
        var actualValue = await Sut.GetAsync<int>(_cacheKey);
        actualValue.Should().Be(default);
    }


    [Theory]
    [InlineData(null, null, true, 0)]
    [InlineData("v1", null, false, 0)]
    [InlineData(null, "myValue", true, 1)]
    public Task GetOrAdd_works_as_expected_timespan_expiration(string? redisReturn, string? generatorReturn, bool expectedGeneratorCall, int stringSetCalls) =>
        GetOrAdd_works_as_expected(redisReturn, generatorReturn, expectedGeneratorCall, stringSetCalls, typeof(TimeSpan));

    [Theory]
    [InlineData(null, null, true, 0)]
    [InlineData("v1", null, false, 0)]
    [InlineData(null, "myValue", true, 1)]
    public Task GetOrAdd_works_as_expected_datetime_expiration(string? redisReturn, string? generatorReturn, bool expectedGeneratorCall, int stringSetCalls) =>
        GetOrAdd_works_as_expected(redisReturn, generatorReturn, expectedGeneratorCall, stringSetCalls, typeof(DateTimeOffset));

    [Theory]
    [InlineData(null, null, true, 0)]
    [InlineData("v1", null, false, 0)]
    [InlineData(null, "myValue", true, 1)]
    public Task GetOrAdd_works_as_expected_no_expiration(string? redisReturn, string? generatorReturn, bool expectedGeneratorCall, int stringSetCalls) =>
        GetOrAdd_works_as_expected(redisReturn, generatorReturn, expectedGeneratorCall, stringSetCalls, typeof(object));

    [Fact]
    public async Task GetOrAdd_null_generator()
    {
        Func<ValueTask<string?>> generator = default!;
        Func<Task> act = async () => await Sut.GetOrAddAsync(_fixture.Create<string>(), generator, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Refresh_timespan_works_as_expected()
    {
        var expiration =  _clock.UtcNow.AddDays(1);
        var actualKey = string.Empty;
        bool called = false;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                called = true;
                return Task.FromResult(_fixture.Create<bool>());
            });
        await Sut.RefreshAsync<string>(_cacheKey, expiration);
        called.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_datetime_works_as_expected()
    {
        var expiration = _fixture.Create<TimeSpan>();
        var actualKey = string.Empty;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                return _fixture.Create<bool>();
            });
        await Sut.RefreshAsync<string>(_cacheKey, expiration);
        await _database.Received(1).KeyExpireAsync(_redisKey, Arg.Any<DateTime>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Refresh_no_expiration_works_as_expected()
    {
        var expiration = _fixture.Create<TimeSpan>();
        var actualKey = string.Empty;
        DateTime? actualExpiration = default;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                actualExpiration = ci.Arg<DateTime?>();
                return _fixture.Create<bool>();
            });
        await Sut.RefreshAsync<string>(_cacheKey, CancellationToken.None);
        await _database.Received(1).KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>());
        actualExpiration.Should().NotBeNull();
        DateTimeOffset actualOffset = DateTime.SpecifyKind(actualExpiration!.Value, DateTimeKind.Utc);
        var expected = _now.Add(_cacheOptions.DefaultExpiration.GetValueOrDefault());
        actualOffset.Should().Be(expected);
    }

    [Theory]
    [InlineData(typeof(TimeSpan))]
    [InlineData(typeof(DateTimeOffset))]
    [InlineData(typeof(object))]
    public async Task Refresh_no_expiration_no_default(Type expirationType)
    {
        _cacheOptions.DefaultExpiration = null;
        if (expirationType == typeof(TimeSpan))
        {
            await Sut.RefreshAsync<string>(_cacheKey, default(TimeSpan?), CancellationToken.None);
        }
        else if (expirationType == typeof(DateTimeOffset))
        {
            await Sut.RefreshAsync<string>(_cacheKey, default(DateTimeOffset?), CancellationToken.None);
        }
        else
        {
            await Sut.RefreshAsync<string>(_cacheKey, CancellationToken.None);
        }

        await _database.DidNotReceive().KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>());
        await _database.Received(1).KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetOrAdd_default_key_expiration()
    {
        DateTime? actualExpiration = default;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                actualExpiration = ci.Arg<DateTime?>();
                return _fixture.Create<bool>();
            });
        await Sut.GetOrAddAsync(_cacheKey, () => ValueTask.FromResult(_fixture.Create<string?>()), default(DateTimeOffset?), CancellationToken.None);
        var expectedExpiration = _clock.UtcNow.Add(_cacheOptions.DefaultExpiration!.Value).Subtract(_clock.UtcNow);

        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Is<TimeSpan?>(t => expectedExpiration == t), When.Always, CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task GetOrAdd_key_expiration_no_default_expiration()
    {
        _cacheOptions.DefaultExpiration = null;
        DateTime? actualExpiration = default;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                actualExpiration = ci.Arg<DateTime?>();
                return _fixture.Create<bool>();
            });
        DateTimeOffset? expiration = _fixture.Create<DateTimeOffset>();
        await Sut.GetOrAddAsync(_cacheKey, () => ValueTask.FromResult(_fixture.Create<string?>()), expiration, CancellationToken.None);
        var expectedExpiration = expiration.Value.Subtract(_clock.UtcNow);
        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Is<TimeSpan?>(t => expectedExpiration == t), When.Always, CommandFlags.DemandMaster);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(5)]
    public async Task Refresh_redis_exception(int? expirationMinutes)
    {
        TimeSpan? expiration = null;
        TimeSpan expectedExpiration;
        if (expirationMinutes.HasValue)
        {
            expiration = TimeSpan.FromMinutes(expirationMinutes.Value);
            expectedExpiration = expiration.Value;
        }
        else
        {
            expectedExpiration = _cacheOptions.DefaultExpiration ?? TimeSpan.MinValue;
        }

        DateTime? actualExpiration = default;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster| CommandFlags.FireAndForget)
                .ThrowsAsync(ci =>
                {
                    actualExpiration = ci.Arg<DateTime?>();
                    return new RedisException("test");
                });
        await Sut.RefreshAsync<string>(_cacheKey, expiration);
        actualExpiration.GetValueOrDefault().Subtract(_now.UtcDateTime).Should().BeCloseTo(expectedExpiration, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Remove_redis_exception()
    {
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
                .ThrowsAsync(ci =>
                {
                    return new RedisException("test");
                });
        var actualResponse = await Sut.RemoveAsync<string>(_cacheKey);
        actualResponse.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_works_as_expected()
    {
        var expectedResponse = _fixture.Create<bool>();
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
                .Returns(expectedResponse);
        var actualResponse = await Sut.RemoveAsync<string>(_cacheKey);
        actualResponse.Should().Be(expectedResponse);
    }

    [Fact]
    public Task Set_works_as_expected_timespan_expiration() =>
        Set_works_as_expected(typeof(TimeSpan));

    [Fact]
    public Task Set_works_as_expected_datetime_expiration() =>
        Set_works_as_expected(typeof(DateTimeOffset));

    [Fact]
    public Task Set_works_as_expected_no_expiration() =>
        Set_works_as_expected(typeof(object));

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    public async Task Set_invalid_key(string key)
    {
        CacheKey cacheKey = key;
        Func<Task> act = async () => { await Sut.SetAsync(cacheKey, _fixture.Create<string>(), _fixture.Create<TimeSpan>()); };
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Set_redis_exception()
    {
        var value = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan>();
        _database.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), CommandFlags.DemandMaster)
                .ThrowsAsync(ci =>
                {
                    return new RedisException("test");
                });
        var actualResponse = await Sut.SetAsync(_cacheKey, value, expiration);
        actualResponse.Should().BeFalse();
    }

    [Fact]
    public async Task Set_default_key()
    {
        var value = default(string);
        var expiration = _fixture.Create<TimeSpan>();
        var expectedResponse = _fixture.Create<bool>();
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
                .Returns(ci =>
                {
                    return Task.FromResult(expectedResponse);
                });
        var actualResponse = await Sut.SetAsync(_cacheKey, value, expiration);
        actualResponse.Should().Be(expectedResponse);
    }


    [Fact]
    public async Task Contains_works_as_expected()
    {
        var cacheKeyCalled = false;
        _database.KeyExistsAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(ci =>
            {
                cacheKeyCalled = true;
                return Task.FromResult(true);
            });

        var actual = await Sut.ContainsAsync<string>(_cacheKey, CancellationToken.None);
        cacheKeyCalled.Should().BeTrue();
        actual.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_redis_exception()
    {
        _database.KeyExistsAsync(Arg.Any<RedisKey>(), CommandFlags.PreferReplica)
            .ThrowsAsync(new Exception());

        var actual = await Sut.ContainsAsync<string>(_cacheKey, CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Unknown_Key_v7()
    {
        var wasCalled = false;
        _cacheOptions.Version = 7;
        _database.KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                wasCalled = true;
                return Task.FromResult(default(DateTime?));
            });
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, CancellationToken.None);
        wasCalled.Should().BeTrue();
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Unknown_Key()
    {
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(default(TimeSpan?));
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, CancellationToken.None);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Known_Key_v7()
    {
        _cacheOptions.Version = 7;
        DateTimeOffset? expected = _fixture.Create<DateTimeOffset>();
        _database.KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns((DateTime?)expected.GetValueOrDefault().UtcDateTime);
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, CancellationToken.None);
        await _database.Received(1).KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().Be(expected.GetValueOrDefault());
    }

    [Fact]
    public async Task Read_ExpireTime_For_Known_Key()
    {
        TimeSpan? expected = _fixture.Create<TimeSpan>();
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(expected);
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, CancellationToken.None);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().Be(_clock.UtcNow.Add(expected.Value));
    }

    [Fact]
    public async Task Read_TimeToLive_For_Unknown_Key()
    {
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(default(TimeSpan?));
        var actual = await Sut.TimeToLiveAsync<string>(_cacheKey, CancellationToken.None);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_TimeToLive_For_Known_Key()
    {
        TimeSpan? expected = _fixture.Create<TimeSpan>();
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(expected);
        var actual = await Sut.TimeToLiveAsync<string>(_cacheKey, CancellationToken.None);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().Be(expected.GetValueOrDefault());
    }

    [Fact]
    public async Task Read_TimeToLive_throws_Exception()
    {
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .ThrowsAsync<Exception>();
        var actual = await Sut.TimeToLiveAsync<string>(_cacheKey, CancellationToken.None);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().Be(null);
    }

    [Fact]
    public async Task Read_ExpireTime_throw_exception_v7()
    {
        _cacheOptions.Version = 7;
        _database.KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>())
            .ThrowsAsync(new Exception());
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, CancellationToken.None);
        await _database.Received(1).KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().BeNull();
    }

    [Fact]
    public void Dispose_can_be_called()
    {
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _prefix = "test";
        _cacheKey = _fixture.Create<string>();
        _redisKey = string.Join(':', _prefix, RedisTypePrefixes.String, _cacheKey).ToLowerInvariant();
        _clock = _fixture.Freeze<ISystemClock>();
        _clock.UtcNow.Returns(c => _now);
        _policyHolder = _fixture.Freeze<IPolicyHolder>();
        var noOpExecutor = new NoOpExecutor();
        _policyHolder.Read.Returns(noOpExecutor);
        _policyHolder.Write.Returns(noOpExecutor);
        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        var redisKeyStrategyFactory = _fixture.Create<IRedisKeyStrategyFactory>();
        _redisKeyStrategy = _fixture.Create<IRedisKeyStrategy>();
        _redisKeyStrategy.GetRedisKey(_cacheKey).Returns(_redisKey);
        redisKeyStrategyFactory.Create(Arg.Any<CacheOptions>(), Arg.Any<Type>())
            .Returns(_redisKeyStrategy);
        _cacheKeyStrategy.GetCacheKey<string>(_cacheKey).Returns(_cacheKey);
        _cacheOptions = new RedisCacheOptions
        {
            Clock = _clock,
            CacheKeyStrategy = _cacheKeyStrategy,
            RedisKeyStrategyFactory = redisKeyStrategyFactory
        };

        _database = _fixture.Freeze<IDatabase>();
        _serializer = new SystemJsonSerializerProxy();
        _fixture.Inject(_serializer);
        var opt = Options.Create(_cacheOptions);
        _fixture.Inject(opt);
        _fixture.Inject(_cacheOptions);

        return Task.CompletedTask;
    }

    private async Task GetOrAdd_works_as_expected(string? redisReturn, string? generatorReturn, bool expectedGeneratorCall, int stringSetCalls, Type expirationType)
    {
        var generatorWasCalled = false;
        _database.StringGetAsync(_redisKey, Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.PreferReplica)))
            .Returns(_ => (RedisValue)_serializer.Serialize(redisReturn));

        string? actualValue = default;
        if (expirationType == typeof(TimeSpan))
        {
            actualValue = await Sut.GetOrAddAsync(_cacheKey, () => { generatorWasCalled = true; return ValueTask.FromResult(generatorReturn); }, _fixture.Create<TimeSpan>(), CancellationToken.None);
        }
        else if (expirationType == typeof(DateTimeOffset))
        {
            actualValue = await Sut.GetOrAddAsync(_cacheKey, () => { generatorWasCalled = true; return ValueTask.FromResult(generatorReturn); }, _clock.UtcNow.Add(TimeSpan.FromSeconds(2)), CancellationToken.None);
        }
        else
        {
            actualValue = await Sut.GetOrAddAsync(_cacheKey, () => { generatorWasCalled = true; return ValueTask.FromResult(generatorReturn); }, CancellationToken.None);
        }
        actualValue.Should().Be(redisReturn ?? generatorReturn);
        generatorWasCalled.Should().Be(expectedGeneratorCall);
        await _database.Received(stringSetCalls).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    private async Task Set_works_as_expected(Type expirationType)
    {
        var value = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan>();
        var expectedResponse = _fixture.Create<bool>();
        _database.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)))
                .Returns(ci =>
                {
                    return Task.FromResult(expectedResponse);
                });
        bool? actualResponse = default;
        if (expirationType == typeof(TimeSpan))
        {
            actualResponse = await Sut.SetAsync(_cacheKey, value, _fixture.Create<TimeSpan>(), CancellationToken.None);
        }
        else if (expirationType == typeof(DateTimeOffset))
        {
            actualResponse = await Sut.SetAsync(_cacheKey, value, _fixture.Create<DateTimeOffset>(), CancellationToken.None);
        }
        else
        {
            actualResponse = await Sut.SetAsync(_cacheKey, value, CancellationToken.None);
        }

        actualResponse.Should().Be(expectedResponse);
        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)));
    }
}
