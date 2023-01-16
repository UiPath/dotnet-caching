using Microsoft.Extensions.Internal;
using NSubstitute.ExceptionExtensions;
using NSubstitute.ReceivedExtensions;
using StackExchange.Redis;

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

    private RedisCache? _sut = null;
    private RedisCache Sut => _sut ??= _fixture.Create<RedisCache>();

    [Fact]
    public async Task Get_works_as_expected()
    {
        var key = _fixture.Create<string>();
        var expectedValue = _fixture.Create<string>();
        var actualKey = string.Empty;
        CommandFlags actualFlags = default;
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                actualKey = ((RedisKey)ci[0]).ToString();
                actualFlags = (CommandFlags)ci[1];
                return _serializer.Serialize(expectedValue);
            });
        var actualValue = await Sut.GetAsync<string>(key);
        actualValue.Should().Be(expectedValue);
        var expectedKey = string.Concat(_cacheOptions.InstanceName, key).ToLowerInvariant();
        actualKey.Should().Be(expectedKey);
        actualFlags.Should().BeOneOf(CommandFlags.PreferReplica);
        Sut.InstanceName.Should().Be(_cacheOptions.InstanceName);
    }

    [Fact]
    public async Task Get_has_no_redis_exceptions()
    {
        var key = _fixture.Create<string>();
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));
        var actualValue = await Sut.GetAsync<int>(key);
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
        Func<Task<string?>> generator = default!;
        Func<Task> act = async () => await Sut.GetOrAddAsync(_fixture.Create<string>(), generator, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Refresh_timespan_works_as_expected()
    {
        var key = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan>();
        var actualKey = string.Empty;
        TimeSpan? actualExpiration = default;
        CommandFlags actualFlags = default;
        _database.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                actualKey = ci.Arg<RedisKey>().ToString();
                actualFlags = ci.Arg<CommandFlags>();
                actualExpiration = ci.Arg<TimeSpan?>();
                return Task.FromResult(_fixture.Create<bool>());
            });
        await Sut.RefreshAsync<string>(key, expiration);
    }

    [Fact]
    public async Task Refresh_datetime_works_as_expected()
    {
        var key = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan>();
        var actualKey = string.Empty;
        DateTime? actualExpiration = default;
        CommandFlags actualFlags = default;
        _database.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                actualKey = ci.Arg<RedisKey>().ToString();
                actualFlags = ci.Arg<CommandFlags>();
                actualExpiration = ci.Arg<DateTime?>();
                return _fixture.Create<bool>();
            });
        await Sut.RefreshAsync<string>(key, expiration);
        await _database.Received(1).KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Refresh_no_expiration_works_as_expected()
    {
        var key = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan>();
        var actualKey = string.Empty;
        DateTime? actualExpiration = default;
        CommandFlags actualFlags = default;
        _database.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                actualKey = ci.Arg<RedisKey>().ToString();
                actualFlags = ci.Arg<CommandFlags>();
                actualExpiration = ci.Arg<DateTime?>();
                return _fixture.Create<bool>();
            });
        await Sut.RefreshAsync<string>(key, CancellationToken.None);
        await _database.Received(1).KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>());
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
        var key = _fixture.Create<string>();
        if (expirationType == typeof(TimeSpan))
        {
            await Sut.RefreshAsync<string>(key, default(TimeSpan?), CancellationToken.None);
        }
        else if (expirationType == typeof(DateTimeOffset))
        {
            await Sut.RefreshAsync<string>(key, default(DateTimeOffset?), CancellationToken.None);
        }
        else
        {
            await Sut.RefreshAsync<string>(key, CancellationToken.None);
        }

        await _database.DidNotReceive().KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>());
        await _database.Received(1).KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetOrAdd_default_key_expiration()
    {
        var key = _fixture.Create<string>();
        DateTime? actualExpiration = default;
        _database.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                actualExpiration = ci.Arg<DateTime?>();
                return _fixture.Create<bool>();
            });
        await Sut.GetOrAddAsync(key, () => Task.FromResult(_fixture.Create<string?>()), default(DateTimeOffset?), CancellationToken.None);
        var expectedExpiration = _clock.UtcNow.Add(_cacheOptions.DefaultExpiration!.Value).Subtract(_clock.UtcNow);

        await _database.Received(1).StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Is<TimeSpan?>(t => expectedExpiration == t), When.Always, CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task GetOrAdd_key_expiration_no_default_expiration()
    {
        _cacheOptions.DefaultExpiration = null;
        var key = _fixture.Create<string>();
        DateTime? actualExpiration = default;
        _database.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                actualExpiration = ci.Arg<DateTime?>();
                return _fixture.Create<bool>();
            });
        DateTimeOffset? expiration = _fixture.Create<DateTimeOffset>();
        await Sut.GetOrAddAsync(key, () => Task.FromResult(_fixture.Create<string?>()), expiration, CancellationToken.None);
        var expectedExpiration = expiration.Value.Subtract(_clock.UtcNow);
        await _database.Received(1).StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Is<TimeSpan?>(t => expectedExpiration == t), When.Always, CommandFlags.DemandMaster);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(5)]
    public async Task Refresh_redis_exception(int? expirationMinutes)
    {
        var key = _fixture.Create<string>();
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
        var actualKey = string.Empty;
        DateTime? actualExpiration = default;
        CommandFlags actualFlags = default;
        _database.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
                .ThrowsAsync(ci =>
                {
                    actualKey = ci.Arg<RedisKey>().ToString();
                    actualFlags = ci.Arg<CommandFlags>();
                    actualExpiration = ci.Arg<DateTime?>();
                    return new RedisException("test");
                });
        await Sut.RefreshAsync<string>(key, expiration);
        actualExpiration.GetValueOrDefault().Subtract(_now.UtcDateTime).Should().BeCloseTo(expectedExpiration, TimeSpan.FromSeconds(10));
        var expectedKey = string.Concat(_cacheOptions.InstanceName, key).ToLowerInvariant();
        actualKey.Should().Be(expectedKey);
        actualFlags.Should().HaveFlag(CommandFlags.DemandMaster);
        actualFlags.Should().HaveFlag(CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task Remove_redis_exception()
    {
        var key = _fixture.Create<string>();
        var actualKey = string.Empty;
        CommandFlags actualFlags = default;
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .ThrowsAsync(ci =>
                {
                    actualKey = ci.Arg<RedisKey>().ToString();
                    actualFlags = ci.Arg<CommandFlags>();
                    return new RedisException("test");
                });
        var actualResponse = await Sut.RemoveAsync<string>(key);
        var expectedKey = string.Concat(_cacheOptions.InstanceName, key).ToLowerInvariant();
        actualKey.Should().Be(expectedKey);
        actualResponse.Should().BeFalse();
        actualFlags.Should().HaveFlag(CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task Remove_works_as_expected()
    {
        var key = _fixture.Create<string>();
        var actualKey = string.Empty;
        CommandFlags actualFlags = default;
        var expectedResponse = _fixture.Create<bool>();
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .ReturnsForAnyArgs(ci =>
                {
                    actualKey = ci.Arg<RedisKey>().ToString();
                    actualFlags = ci.Arg<CommandFlags>();
                    return Task.FromResult(expectedResponse);
                });
        var actualResponse = await Sut.RemoveAsync<string>(key);
        var expectedKey = string.Concat(_cacheOptions.InstanceName, key).ToLowerInvariant();
        actualKey.Should().Be(expectedKey);
        actualResponse.Should().Be(expectedResponse);
        actualFlags.Should().HaveFlag(CommandFlags.DemandMaster);
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
        Func<Task> act = async () => { await Sut.SetAsync(key, _fixture.Create<string>(), _fixture.Create<TimeSpan>()); };
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Set_redis_exception()
    {
        var key = _fixture.Create<string>();
        var value = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan>();
        var actualKey = string.Empty;
        CommandFlags actualFlags = default;
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
                .ThrowsAsync(ci =>
                {
                    actualKey = ci.Arg<RedisKey>().ToString();
                    actualFlags = ci.Arg<CommandFlags>();
                    return new RedisException("test");
                });
        var actualResponse = await Sut.SetAsync(key, value, expiration);
        var expectedKey = string.Concat(_cacheOptions.InstanceName, key).ToLowerInvariant();
        actualKey.Should().Be(expectedKey);
        actualResponse.Should().BeFalse();
        actualFlags.Should().HaveFlag(CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task Set_default_key()
    {
        var key = _fixture.Create<string>();
        var value = default(string);
        var expiration = _fixture.Create<TimeSpan>();
        var actualKey = string.Empty;
        CommandFlags actualFlags = default;
        var expectedResponse = _fixture.Create<bool>();
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .ReturnsForAnyArgs(ci =>
                {
                    actualKey = ci.Arg<RedisKey>().ToString();
                    actualFlags = ci.Arg<CommandFlags>();
                    return Task.FromResult(expectedResponse);
                });
        var actualResponse = await Sut.SetAsync(key, value, expiration);
        var expectedKey = string.Concat(_cacheOptions.InstanceName, key).ToLowerInvariant();
        actualKey.Should().Be(expectedKey);
        actualResponse.Should().Be(expectedResponse);
        actualFlags.Should().HaveFlag(CommandFlags.DemandMaster);
    }


    [Fact]
    public async Task Contains_works_as_expected()
    {
        var key = _fixture.Create<string>();
        var keysCalled = false;
        _database.KeyExistsAsync(Arg.Any<RedisKey>(), CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(ci =>
            {
                keysCalled = true;
                return Task.FromResult(true);
            });

        var actual = await Sut.ContainsAsync(key, CancellationToken.None);
        keysCalled.Should().BeTrue();
        actual.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_redis_exception()
    {
        var key = _fixture.Create<string>();
        _database.KeyExistsAsync(Arg.Any<RedisKey>(), CommandFlags.PreferReplica)
            .ThrowsAsync(new Exception());

        var actual = await Sut.ContainsAsync(key, CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Unknown_Key_v7()
    {
        var key = _fixture.Create<string>();
        var wasCalled = false;
        _cacheOptions.Version = 7;
        _database.KeyExpireTimeAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                wasCalled = true;
                return Task.FromResult(default(DateTime?));
            });
        var actual = await Sut.ExpireTimeAsync(key, CancellationToken.None);
        wasCalled.Should().BeTrue();
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Unknown_Key()
    {
        var key = _fixture.Create<string>();
        var wasCalled = false;
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                wasCalled = true;
                return Task.FromResult(default(TimeSpan?));
            });
        var actual = await Sut.ExpireTimeAsync(key, CancellationToken.None);
        wasCalled.Should().BeTrue();
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Known_Key_v7()
    {
        var key = _fixture.Create<string>();
        var wasCalled = false;
        _cacheOptions.Version = 7;
        DateTimeOffset? expected = _fixture.Create<DateTimeOffset>();
        _database.KeyExpireTimeAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                wasCalled = true;
                return Task.FromResult((DateTime?)expected.GetValueOrDefault().UtcDateTime);
            });
        var actual = await Sut.ExpireTimeAsync(key, CancellationToken.None);
        wasCalled.Should().BeTrue();
        actual.Should().Be(expected.GetValueOrDefault());
    }

    [Fact]
    public async Task Read_ExpireTime_For_Known_Key()
    {
        var key = _fixture.Create<string>();
        var wasCalled = false;
        TimeSpan? expected = _fixture.Create<TimeSpan>();
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                wasCalled = true;
                return Task.FromResult(expected);
            });
        var actual = await Sut.ExpireTimeAsync(key, CancellationToken.None);
        wasCalled.Should().BeTrue();
        actual.Should().Be(_clock.UtcNow.Add(expected.Value));
    }

    [Fact]
    public async Task Read_TimeToLive_For_Unknown_Key()
    {
        var key = _fixture.Create<string>();
        var wasCalled = false;
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                wasCalled = true;
                return Task.FromResult(default(TimeSpan?));
            });
        var actual = await Sut.TimeToLiveAsync(key, CancellationToken.None);
        wasCalled.Should().BeTrue();
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_TimeToLive_For_Known_Key()
    {
        var key = _fixture.Create<string>();
        var wasCalled = false;
        TimeSpan? expected = _fixture.Create<TimeSpan>();
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                wasCalled = true;
                return Task.FromResult(expected);
            });
        var actual = await Sut.TimeToLiveAsync(key, CancellationToken.None);
        wasCalled.Should().BeTrue();
        actual.Should().Be(expected.GetValueOrDefault());
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _clock = _fixture.Freeze<ISystemClock>();
        _clock.UtcNow.Returns(c => _now);
        _policyHolder = _fixture.Freeze<IPolicyHolder>();
        _policyHolder.AsyncPolicy.Returns(Polly.Policy.NoOpAsync());
        _cacheOptions = new RedisCacheOptions
        {
            Clock = _clock
        };

        _database = _fixture.Freeze<IDatabase>();
        _serializer = new SystemJsonSerializerProxy();
        _fixture.Inject(_serializer);
        _fixture.Inject(Options.Create(_cacheOptions));


        return Task.CompletedTask;
    }

    private async Task GetOrAdd_works_as_expected(string? redisReturn, string? generatorReturn, bool expectedGeneratorCall, int stringSetCalls, Type expirationType)
    {
        var key = _fixture.Create<string>();
        var actualKey = string.Empty;
        CommandFlags actualFlags = default;
        var generatorWasCalled = false;
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                actualKey = ci.Arg<RedisKey>().ToString();
                actualFlags = ci.Arg<CommandFlags>();
                return (RedisValue)_serializer.Serialize(redisReturn);
            });

        string? actualValue = default;
        if (expirationType == typeof(TimeSpan))
        {
            actualValue = await Sut.GetOrAddAsync(key, () => { generatorWasCalled = true; return Task.FromResult(generatorReturn); }, _fixture.Create<TimeSpan>(), CancellationToken.None);
        }
        else if (expirationType == typeof(DateTimeOffset))
        {
            actualValue = await Sut.GetOrAddAsync(key, () => { generatorWasCalled = true; return Task.FromResult(generatorReturn); }, _clock.UtcNow.Add(TimeSpan.FromSeconds(2)), CancellationToken.None);
        }
        else
        {
            actualValue = await Sut.GetOrAddAsync(key, () => { generatorWasCalled = true; return Task.FromResult(generatorReturn); }, CancellationToken.None);
        }
        actualValue.Should().Be(redisReturn ?? generatorReturn);
        var expectedKey = string.Concat(_cacheOptions.InstanceName, key).ToLowerInvariant();
        actualKey.Should().Be(expectedKey);
        actualFlags.Should().BeOneOf(CommandFlags.PreferReplica);
        generatorWasCalled.Should().Be(expectedGeneratorCall);
        await _database.Received(stringSetCalls).StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    private async Task Set_works_as_expected(Type expirationType)
    {
        var key = _fixture.Create<string>();
        var value = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan>();
        var actualKey = string.Empty;
        CommandFlags actualFlags = default;
        var expectedResponse = _fixture.Create<bool>();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
                .ReturnsForAnyArgs(ci =>
                {
                    actualKey = ci.Arg<RedisKey>().ToString();
                    actualFlags = ci.Arg<CommandFlags>();
                    return Task.FromResult(expectedResponse);
                });
        bool? actualResponse = default;
        if (expirationType == typeof(TimeSpan))
        {
            actualResponse = await Sut.SetAsync(key, value, _fixture.Create<TimeSpan>(), CancellationToken.None);
        }
        else if (expirationType == typeof(DateTimeOffset))
        {
            actualResponse = await Sut.SetAsync(key, value, _fixture.Create<DateTimeOffset>(), CancellationToken.None);
        }
        else
        {
            actualResponse = await Sut.SetAsync(key, value, CancellationToken.None);
        }

        var expectedKey = string.Concat(_cacheOptions.InstanceName, key).ToLowerInvariant();
        actualKey.Should().Be(expectedKey);
        actualResponse.Should().Be(expectedResponse);
        actualFlags.Should().HaveFlag(CommandFlags.DemandMaster);
    }
}
