using Microsoft.Extensions.Internal;
using NSubstitute.ExceptionExtensions;
using NSubstitute.ReceivedExtensions;
using StackExchange.Redis;
using UiPath.Platform.Caching;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Tests.Redis;

public class RedisCacheTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private ISystemClock _clock = default!;
    private IResiliencePipelineHolder _resiliencePipelineHolder = default!;
    private RedisCacheOptions _cacheOptions = default!;
    private IDatabase _database = default!;
    private ITransaction _transaction = default!;
    private ISerializerProxy<RedisValue> _serializer = default!;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private CacheKey _cacheKey = default!;
    private RedisKey _redisKey = default!;
    private CacheKey _multiKey = default!;
    private RedisKey _redisMultiKey = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private IRedisKeyStrategy _redisKeyStrategy = default!;
    private string _prefix = default!;
    private IRedisConnector _connector = default!;
    private bool _isConnected = true;
    private Version _version = new(6, 0);
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
        var actualValue = await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        actualValue.Should().Be(expectedValue);
        Sut.Name.Should().Be("Redis");
    }

    [Fact]
    public async Task Get_works_as_expected_when_disconnected()
    {
        _isConnected = false;
        var value = _fixture.Create<string>();
        _database.StringGetAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(ci =>
            {
                return _serializer.Serialize(value);
            });
        var actualValue = await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        actualValue.Should().Be(null);
    }

    [Fact]
    public async Task Get_has_no_redis_exceptions()
    {
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));
        var actualValue = await Sut.GetAsync<int?>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        actualValue.Should().Be(default(int?));
    }

    [Fact]
    public async Task Multi_get_works_as_expected()
    {
        var expectedValue = _fixture.Create<string>();
        _database.StringGetAsync(Arg.Is<RedisKey[]>(k => k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferReplica)
            .Returns(ci =>
            {
                return new RedisValue[] { _serializer.Serialize(expectedValue), _serializer.Serialize(expectedValue) };
            });
        var actualValue = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        actualValue.Should().BeEquivalentTo(new KeyValuePair<CacheKey, string>[] { new(_cacheKey, expectedValue), new(_multiKey, expectedValue) });
    }

    [Fact]
    public async Task Multi_get_has_no_redis_exceptions()
    {
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));
        var actualValue = await Sut.GetAsync<int?>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        actualValue.Should().BeEquivalentTo(new KeyValuePair<CacheKey, int?>[] { new(_cacheKey, default), new(_multiKey, default) });
    }

    [Fact]
    public async Task GetCacheEntry_bundles_get_and_pttl_in_single_transaction()
    {
        var expectedValue = _fixture.Create<string>();
        var expectedTtl = TimeSpan.FromMinutes(15);
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(_serializer.Serialize(expectedValue));
        _transaction.KeyTimeToLiveAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(expectedTtl);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Value.Should().Be(expectedValue);
        entry.Expiration.Should().Be(_clock.UtcNow.Add(expectedTtl));
        // Bundled in transaction; no separate StringGetAsync against the database directly.
        await _database.DidNotReceive().StringGetAsync(_redisKey, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetCacheEntry_uses_KeyExpireTimeAsync_on_redis_v7()
    {
        _version = new(7, 0);
        var expectedValue = _fixture.Create<string>();
        var expectedExpiration = DateTimeOffset.UtcNow.AddMinutes(15);
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(_serializer.Serialize(expectedValue));
        _transaction.KeyExpireTimeAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(expectedExpiration.UtcDateTime);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Value.Should().Be(expectedValue);
        entry.Expiration.Should().BeCloseTo(expectedExpiration, TimeSpan.FromSeconds(1));
        await _transaction.Received(1).KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>());
        await _transaction.DidNotReceive().KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetCacheEntry_returns_default_when_disconnected()
    {
        _isConnected = false;
        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        entry.Should().NotBeNull();
        entry.Value.Should().BeNull();
        await _transaction.DidNotReceive().ExecuteAsync(Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetCacheEntries_bundles_mget_and_pttls_in_single_transaction()
    {
        var expected = _fixture.Create<string>();
        var expectedTtl = TimeSpan.FromMinutes(15);
        _transaction.StringGetAsync(Arg.Is<RedisKey[]>(k => k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferReplica)
            .Returns(new RedisValue[] { _serializer.Serialize(expected), _serializer.Serialize(expected) });
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferReplica)
            .Returns(expectedTtl);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries[0].Value.Value.Should().Be(expected);
        entries[1].Value.Value.Should().Be(expected);
        entries[0].Value.Expiration.Should().Be(_clock.UtcNow.Add(expectedTtl));
        // Single MGET inside the transaction; PTTLs are also inside the transaction.
        await _database.DidNotReceive().StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
        await _database.DidNotReceive().KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetCacheEntries_returns_defaults_when_disconnected()
    {
        _isConnected = false;
        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        entries.Should().HaveCount(2);
        entries.Should().AllSatisfy(kv => kv.Value.Value.Should().BeNull());
    }

    [Fact]
    public async Task GetCacheEntry_validates_null_key_when_disconnected()
    {
        _isConnected = false;
        Func<Task> act = async () => await Sut.GetCacheEntryAsync<string>((string?)null!, policy: null, token: testContextAccessor.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetCacheEntries_validates_null_key_when_disconnected()
    {
        _isConnected = false;
        Func<Task> act = async () => await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { (string?)null! }, policy: null, token: testContextAccessor.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetCacheEntry_returns_default_expiration_when_remote_has_no_ttl_v7()
    {
        _version = new(7, 0);
        var expectedValue = _fixture.Create<string>();
        _cacheOptions.DefaultExpiration = TimeSpan.FromHours(1);
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(_serializer.Serialize(expectedValue));
        _transaction.KeyExpireTimeAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(default(DateTime?));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Value.Should().Be(expectedValue);
        // Both Redis 6 and Redis 7 paths must converge on UtcNow + DefaultExpiration when the remote has no TTL.
        entry.Expiration.Should().Be(_clock.UtcNow.Add(TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task GetCacheEntry_returns_max_value_when_remote_has_no_ttl_and_no_default_v7()
    {
        _version = new(7, 0);
        var expectedValue = _fixture.Create<string>();
        _cacheOptions.DefaultExpiration = null;
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(_serializer.Serialize(expectedValue));
        _transaction.KeyExpireTimeAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(default(DateTime?));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Value.Should().Be(expectedValue);
        entry.Expiration.Should().Be(DateTimeOffset.MaxValue);
    }

    [Fact]
    public async Task GetCacheEntries_returns_defaults_on_redis_exception()
    {
        _transaction.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        var entries = await Sut.GetCacheEntriesAsync<int?>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        entries.Should().HaveCount(2);
        entries.Should().AllSatisfy(kv => kv.Value.Value.Should().BeNull());
    }

    [Fact]
    public async Task GetCacheEntry_returns_default_on_redis_exception()
    {
        _transaction.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        var entry = await Sut.GetCacheEntryAsync<int?>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        entry.Should().NotBeNull();
        entry.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetCacheEntry_routes_transaction_to_replica()
    {
        var expectedValue = _fixture.Create<string>();
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(_serializer.Serialize(expectedValue));
        _transaction.KeyTimeToLiveAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(TimeSpan.FromMinutes(15));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        // Inner queued flags do not propagate to transaction routing in SE.Redis;
        // the flag must be passed to ExecuteAsync explicitly so the transaction lands on a replica.
        await _transaction.Received(1).ExecuteAsync(CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task GetCacheEntries_routes_transaction_to_replica()
    {
        _transaction.StringGetAsync(Arg.Any<RedisKey[]>(), CommandFlags.PreferReplica)
            .Returns(new RedisValue[] { RedisValue.Null, RedisValue.Null });
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferReplica)
            .Returns(TimeSpan.FromMinutes(15));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        await _transaction.Received(1).ExecuteAsync(CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task GetCacheEntries_uses_KeyExpireTimeAsync_on_redis_v7()
    {
        _version = new(7, 0);
        var expected = _fixture.Create<string>();
        var expectedExpiration = DateTimeOffset.UtcNow.AddMinutes(15);
        _transaction.StringGetAsync(Arg.Is<RedisKey[]>(k => k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferReplica)
            .Returns(new RedisValue[] { _serializer.Serialize(expected), _serializer.Serialize(expected) });
        _transaction.KeyExpireTimeAsync(Arg.Any<RedisKey>(), CommandFlags.PreferReplica)
            .Returns(expectedExpiration.UtcDateTime);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries[0].Value.Value.Should().Be(expected);
        entries[1].Value.Value.Should().Be(expected);
        entries[0].Value.Expiration.Should().BeCloseTo(expectedExpiration, TimeSpan.FromSeconds(1));
        await _transaction.Received(1).KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).KeyExpireTimeAsync(_redisMultiKey, Arg.Any<CommandFlags>());
        await _transaction.DidNotReceive().KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetCacheEntries_returns_empty_array_for_empty_keys()
    {
        var entries = await Sut.GetCacheEntriesAsync<string>([], policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().BeEmpty();
        await _transaction.DidNotReceive().ExecuteAsync(Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetCacheEntry_returns_default_when_transaction_fails()
    {
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(_serializer.Serialize(_fixture.Create<string>()));
        _transaction.KeyTimeToLiveAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(TimeSpan.FromMinutes(15));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Should().NotBeNull();
        entry.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetCacheEntries_returns_defaults_when_transaction_fails()
    {
        _transaction.StringGetAsync(Arg.Any<RedisKey[]>(), CommandFlags.PreferReplica)
            .Returns(new RedisValue[] { _serializer.Serialize(_fixture.Create<string>()), _serializer.Serialize(_fixture.Create<string>()) });
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferReplica)
            .Returns(TimeSpan.FromMinutes(15));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries.Should().AllSatisfy(kv => kv.Value.Value.Should().BeNull());
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
        Func<CancellationToken, Task<string?>> generator = default!;
        Func<Task> act = async () => await Sut.GetOrAddAsync(_fixture.Create<string>(), generator, (CachePolicy?)null, testContextAccessor.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetOrAdd_generator()
    {
        var expectedValue = _fixture.Create<string?>();
        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult(expectedValue);
        var actual = await Sut.GetOrAddAsync(_fixture.Create<string>(), generator, TimeSpan.FromSeconds(5), token: testContextAccessor.Current.CancellationToken);
        actual.Should().Be(expectedValue);
    }

    [Fact]
    public async Task GetOrAdd_when_disconnected_runs_generator_without_redis_probe()
    {
        _isConnected = false;
        var expected = _fixture.Create<string>();
        var generatorCalled = false;
        Func<CancellationToken, Task<string?>> generator = _ =>
        {
            generatorCalled = true;
            return Task.FromResult<string?>(expected);
        };

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, TimeSpan.FromSeconds(5), token: testContextAccessor.Current.CancellationToken);

        actual.Should().Be(expected);
        generatorCalled.Should().BeTrue();
        await _database.DidNotReceive().StringGetAsync(_redisKey, Arg.Any<CommandFlags>());
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
        await Sut.RefreshAsync<string>(_cacheKey, expiration, token: testContextAccessor.Current.CancellationToken);
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
        await Sut.RefreshAsync<string>(_cacheKey, expiration, token: testContextAccessor.Current.CancellationToken);
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
        await Sut.RefreshAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
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
            await Sut.RefreshAsync<string>(_cacheKey, default(TimeSpan?), token: testContextAccessor.Current.CancellationToken);
        }
        else if (expirationType == typeof(DateTimeOffset))
        {
            await Sut.RefreshAsync<string>(_cacheKey, default(DateTimeOffset?), token: testContextAccessor.Current.CancellationToken);
        }
        else
        {
            await Sut.RefreshAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
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
        await Sut.GetOrAddAsync(_cacheKey, _ => Task.FromResult(_fixture.Create<string?>()), expiration: default(DateTimeOffset?), token: testContextAccessor.Current.CancellationToken);
        var expectedExpiration = _clock.UtcNow.Add(_cacheOptions.DefaultExpiration!.Value).Subtract(_clock.UtcNow);

        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Is<TimeSpan?>(t => expectedExpiration == t), When.Always, CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task GetOrAdd_policy_DistributedExpiration_overrides_default_when_caller_omits_expiration()
    {
        var policyTtl = TimeSpan.FromMinutes(7);
        var policy = new CachePolicy { DistributedExpiration = policyTtl };
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
            .Returns(_fixture.Create<bool>());

        await Sut.GetOrAddAsync(_cacheKey, _ => Task.FromResult(_fixture.Create<string?>()), policy: policy, token: testContextAccessor.Current.CancellationToken);

        await _database.Received(1).StringSetAsync(
            _redisKey,
            Arg.Any<RedisValue>(),
            Arg.Is<TimeSpan?>(t => t.HasValue && t.Value > policyTtl - TimeSpan.FromSeconds(5) && t.Value < policyTtl + TimeSpan.FromSeconds(5)),
            When.Always,
            CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task GetOrAdd_policy_FactoryTimeout_cancels_slow_generator()
    {
        var policy = new CachePolicy { FactoryTimeout = TimeSpan.FromMilliseconds(50) };
        Func<CancellationToken, Task<string?>> generator = async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return "never";
        };

        var act = async () => await Sut.GetOrAddAsync(_cacheKey, generator, policy: policy, token: testContextAccessor.Current.CancellationToken);

        await act.Should().ThrowAsync<TimeoutException>();
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
        await Sut.GetOrAddAsync(_cacheKey, _ => Task.FromResult(_fixture.Create<string?>()), expiration: expiration, token: testContextAccessor.Current.CancellationToken);
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
        await Sut.RefreshAsync<string>(_cacheKey, expiration, token: testContextAccessor.Current.CancellationToken);
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
        var actualResponse = await Sut.RemoveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        actualResponse.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_works_as_expected()
    {
        var apiResponse = _fixture.Create<bool>();
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
                .Returns(apiResponse);
        var actualResponse = await Sut.RemoveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        actualResponse.Should().Be(true);
    }

    [Fact]
    public async Task Multi_remove_redis_exception()
    {
        _database.KeyDeleteAsync(Arg.Is<RedisKey[]>(keys => keys.Contains(_redisKey )), CommandFlags.DemandMaster)
                .ThrowsAsync(ci =>
                {
                    return new RedisException("test");
                });
        var actualResponse = await Sut.RemoveAsync<string>(new CacheKey[] { _cacheKey }, testContextAccessor.Current.CancellationToken);
        actualResponse.Should().BeFalse();
    }

    [Fact]
    public async Task Multi_remove_works_as_expected()
    {
        var keyDeleteResponse = _fixture.Create<bool>();
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
                .Returns(keyDeleteResponse);
        var actualResponse = await Sut.RemoveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        actualResponse.Should().Be(true);
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

    [Fact]
    public Task Multi_set_works_as_expected_timespan_expiration() =>
        Multi_set_works_as_expected(typeof(TimeSpan));

    [Fact]
    public Task Multi_set_works_as_expected_datetime_expiration() =>
        Multi_set_works_as_expected(typeof(DateTimeOffset));

    [Fact]
    public Task Multi_set_works_as_expected_no_expiration() =>
        Multi_set_works_as_expected(typeof(object));

    [Fact]
    public async Task Multi_set_when_no_connection()
    {
        _isConnected = false;
        var value = _fixture.Create<string>();
        var actualResponse = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);
        actualResponse.Should().BeFalse();
    }

    [Fact]
    public async Task Multi_set_when_defaultValue()
    {
        string? value = default;
        var actualResponse = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);
        await _transaction.Received().KeyDeleteAsync(Arg.Any<RedisKey>(),Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)));
    }

    [Fact]
    public async Task Multi_set_when_redis_exceptions()
    {
        var value = _fixture.Create<string>();
        _database.CreateTransaction(Arg.Any<object?>()).Throws(new RedisException("test"));
        var actualResponse = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);
        actualResponse.Should().BeFalse();
    }



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
        var actualResponse = await Sut.SetAsync(_cacheKey, value, expiration, token: testContextAccessor.Current.CancellationToken);
        actualResponse.Should().BeFalse();
    }

    [Fact]
    public async Task Set_default_key()
    {
        var value = default(string);
        var expiration = _fixture.Create<TimeSpan>();
        var apiResponse = _fixture.Create<bool>();
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
                .Returns(ci =>
                {
                    return Task.FromResult(apiResponse);
                });
        var actualResponse = await Sut.SetAsync(_cacheKey, value, expiration, token: testContextAccessor.Current.CancellationToken);
        actualResponse.Should().Be(true);
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

        var actual = await Sut.ContainsAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        cacheKeyCalled.Should().BeTrue();
        actual.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_redis_exception()
    {
        _database.KeyExistsAsync(Arg.Any<RedisKey>(), CommandFlags.PreferReplica)
            .ThrowsAsync(new Exception());

        var actual = await Sut.ContainsAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Unknown_Key_v7()
    {
        var wasCalled = false;
        _version = new(7,0);
        _database.KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                wasCalled = true;
                return Task.FromResult(default(DateTime?));
            });
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        wasCalled.Should().BeTrue();
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Unknown_Key()
    {
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(default(TimeSpan?));
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Known_Key_v7()
    {
        _version = new(7, 0);
        DateTimeOffset? expected = _fixture.Create<DateTimeOffset>();
        _database.KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns((DateTime?)expected.GetValueOrDefault().UtcDateTime);
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().Be(expected.GetValueOrDefault());
    }

    [Fact]
    public async Task Read_ExpireTime_For_Known_Key()
    {
        TimeSpan? expected = _fixture.Create<TimeSpan>();
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(expected);
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().Be(_clock.UtcNow.Add(expected.Value));
    }

    [Fact]
    public async Task Read_TimeToLive_For_Unknown_Key()
    {
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(default(TimeSpan?));
        var actual = await Sut.TimeToLiveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_TimeToLive_For_Known_Key()
    {
        TimeSpan? expected = _fixture.Create<TimeSpan>();
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(expected);
        var actual = await Sut.TimeToLiveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().Be(expected.GetValueOrDefault());
    }

    [Fact]
    public async Task Read_TimeToLive_throws_Exception()
    {
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .ThrowsAsync<Exception>();
        var actual = await Sut.TimeToLiveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().Be(null);
    }

    [Fact]
    public async Task Read_ExpireTime_throw_exception_v7()
    {
        _version = new(7, 0);
        _database.KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>())
            .ThrowsAsync(new Exception());
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().BeNull();
    }

    [Fact]
    public void Dispose_can_be_called()
    {
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task GetCacheEntryAsync_returns_miss_for_RedisValue_Null()
    {
        _cacheOptions.CacheNullValues = true;
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(RedisValue.Null);
        _transaction.KeyExpireTimeAsync(_redisKey, CommandFlags.PreferReplica).Returns((DateTime?)null);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Found.Should().BeFalse();
    }

    [Fact]
    public async Task GetCacheEntryAsync_returns_cached_null_when_CacheNullValues_true_and_value_is_empty()
    {
        _cacheOptions.CacheNullValues = true;
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(RedisValue.EmptyString);
        _transaction.KeyExpireTimeAsync(_redisKey, CommandFlags.PreferReplica).Returns(_now.AddMinutes(5).UtcDateTime);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Found.Should().BeTrue("Length==0 with CacheNullValues=true is the explicit cached-null sentinel");
        entry.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetCacheEntryAsync_returns_miss_for_empty_value_when_CacheNullValues_false()
    {
        _cacheOptions.CacheNullValues = false;
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(RedisValue.EmptyString);
        _transaction.KeyExpireTimeAsync(_redisKey, CommandFlags.PreferReplica).Returns(_now.AddMinutes(5).UtcDateTime);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Found.Should().BeFalse();
    }

    [Fact]
    public async Task GetCacheEntriesAsync_returns_cached_null_when_CacheNullValues_true_and_value_is_empty()
    {
        _cacheOptions.CacheNullValues = true;
        var expectedValue = _fixture.Create<string>();
        var expectedTtl = TimeSpan.FromMinutes(15);
        _transaction.StringGetAsync(Arg.Is<RedisKey[]>(k => k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferReplica)
            .Returns(new RedisValue[] { RedisValue.EmptyString, _serializer.Serialize(expectedValue) });
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferReplica).Returns(expectedTtl);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries[0].Value.Found.Should().BeTrue("Length==0 with CacheNullValues=true is the explicit cached-null sentinel in the batched path");
        entries[0].Value.Value.Should().BeNull();
        entries[0].Value.Expiration.Should().Be(_clock.UtcNow.Add(expectedTtl));
        entries[1].Value.Found.Should().BeTrue();
        entries[1].Value.Value.Should().Be(expectedValue);
    }

    [Fact]
    public async Task GetCacheEntriesAsync_returns_miss_for_empty_value_when_CacheNullValues_false()
    {
        _cacheOptions.CacheNullValues = false;
        _transaction.StringGetAsync(Arg.Is<RedisKey[]>(k => k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferReplica)
            .Returns(new RedisValue[] { RedisValue.EmptyString, RedisValue.EmptyString });
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferReplica).Returns(TimeSpan.FromMinutes(15));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries.Should().AllSatisfy(kv => kv.Value.Found.Should().BeFalse("the empty-value sentinel must be ignored when the opt-in is off"));
    }

    [Fact]
    public async Task SetAsync_writes_empty_value_when_CacheNullValues_true_and_value_is_null()
    {
        _cacheOptions.CacheNullValues = true;
        RedisValue? captured = null;
        _database.StringSetAsync(_redisKey, Arg.Do<RedisValue>(v => captured = v), Arg.Any<TimeSpan?>(), When.Always, Arg.Any<CommandFlags>())
            .Returns(true);

        var ok = await Sut.SetAsync<string>(_cacheKey, value: null, policy: null, token: testContextAccessor.Current.CancellationToken);

        ok.Should().BeTrue();
        captured.HasValue.Should().BeTrue();
        captured!.Value.Length().Should().Be(0, "explicit null caching writes Redis EmptyString (Length==0), not a JSON 'null' literal");
        await _database.DidNotReceive().KeyDeleteAsync(_redisKey, Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SetAsync_deletes_key_when_CacheNullValues_false_and_value_is_null()
    {
        _cacheOptions.CacheNullValues = false;
        var ok = await Sut.SetAsync<string>(_cacheKey, value: null, policy: null, token: testContextAccessor.Current.CancellationToken);

        ok.Should().BeTrue();
        await _database.Received().KeyDeleteAsync(_redisKey, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SetAsync_null_with_past_expiration_deletes_even_when_CacheNullValues_true()
    {
        _cacheOptions.CacheNullValues = true;
        var pastExpiration = _clock.UtcNow.AddMinutes(-5);

        var ok = await Sut.SetAsync<string>(_cacheKey, value: null, pastExpiration, token: testContextAccessor.Current.CancellationToken);

        ok.Should().BeTrue();
        await _database.Received().KeyDeleteAsync(_redisKey, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetOrAddAsync_returns_cached_null_without_invoking_generator_when_CacheNullValues_true()
    {
        _cacheOptions.CacheNullValues = true;
        _database.StringGetAsync(_redisKey, Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.PreferReplica)))
            .Returns(RedisValue.EmptyString);
        var generatorCalled = false;

        var ret = await Sut.GetOrAddAsync<string>(
            _cacheKey,
            _ => { generatorCalled = true; return Task.FromResult<string?>("fresh"); },
            (CachePolicy?)null,
            testContextAccessor.Current.CancellationToken);

        ret.Should().BeNull();
        generatorCalled.Should().BeFalse("an explicit cached null must short-circuit the generator");
    }

    [Fact]
    public async Task GetOrAddAsync_stores_null_when_generator_yields_null_and_CacheNullValues_true()
    {
        _cacheOptions.CacheNullValues = true;
        _database.StringGetAsync(_redisKey, Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.PreferReplica)))
            .Returns(RedisValue.Null);
        RedisValue? captured = null;
        _database.StringSetAsync(_redisKey, Arg.Do<RedisValue>(v => captured = v), Arg.Any<TimeSpan?>(), When.Always, Arg.Any<CommandFlags>())
            .Returns(true);

        var ret = await Sut.GetOrAddAsync<string>(
            _cacheKey,
            _ => Task.FromResult<string?>(null),
            (CachePolicy?)null,
            testContextAccessor.Current.CancellationToken);

        ret.Should().BeNull();
        captured.HasValue.Should().BeTrue("a null generator result must be persisted when CacheNullValues=true");
        captured!.Value.Length().Should().Be(0);
    }

    [Fact]
    public async Task GetOrAddAsync_does_not_store_null_when_generator_yields_null_and_CacheNullValues_false()
    {
        _cacheOptions.CacheNullValues = false;
        _database.StringGetAsync(_redisKey, Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.PreferReplica)))
            .Returns(RedisValue.Null);

        var ret = await Sut.GetOrAddAsync<string>(
            _cacheKey,
            _ => Task.FromResult<string?>(null),
            (CachePolicy?)null,
            testContextAccessor.Current.CancellationToken);

        ret.Should().BeNull();
        await _database.DidNotReceiveWithAnyArgs().StringSetAsync(default, default, default, default, default);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _prefix = "test";
        _cacheKey = _fixture.Create<string>();
        _redisKey = string.Join(':', _prefix, RedisTypePrefixes.String, _cacheKey).ToLowerInvariant();
        _multiKey = _fixture.Create<string>();
        _redisMultiKey = string.Join(':', _prefix, RedisTypePrefixes.String, _multiKey).ToLowerInvariant();
        _clock = _fixture.Freeze<ISystemClock>();
        _clock.UtcNow.Returns(c => _now);
        _resiliencePipelineHolder = _fixture.Freeze<IResiliencePipelineHolder>();
        var noOpExecutor = new EmptyResiliencePipeline();
        _resiliencePipelineHolder.Read.Returns(noOpExecutor);
        _resiliencePipelineHolder.Write.Returns(noOpExecutor);
        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        var redisKeyStrategyFactory = _fixture.Create<IRedisKeyStrategyFactory>();
        _redisKeyStrategy = _fixture.Create<IRedisKeyStrategy>();
        _redisKeyStrategy.GetRedisKey(_cacheKey).Returns(_redisKey);
        _redisKeyStrategy.GetRedisKey(_multiKey).Returns(_redisMultiKey);
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
        _transaction = _fixture.Freeze<ITransaction>();
        _database.CreateTransaction().Returns(_transaction);
        _serializer = new SystemJsonSerializerProxy();
        _fixture.Inject(_serializer);
        var opt = Options.Create(_cacheOptions);
        _fixture.Inject(opt);
        _fixture.Inject(_cacheOptions);
        _connector = _fixture.Freeze<IRedisConnector>();
        _connector.Database.Returns(_ => _database);
        _connector.Version.Returns(_ => _version);
        _connector.IsConnected.Returns(ctx => _isConnected);
        return ValueTask.CompletedTask;
    }

    private async Task GetOrAdd_works_as_expected(string? redisReturn, string? generatorReturn, bool expectedGeneratorCall, int stringSetCalls, Type expirationType)
    {
        var generatorWasCalled = false;
        _database.StringGetAsync(_redisKey, Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.PreferReplica)))
            .Returns(_ => (RedisValue)_serializer.Serialize(redisReturn));

        string? actualValue = default;
        if (expirationType == typeof(TimeSpan))
        {
            actualValue = await Sut.GetOrAddAsync(_cacheKey, _ => { generatorWasCalled = true; return Task.FromResult(generatorReturn); }, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);
        }
        else if (expirationType == typeof(DateTimeOffset))
        {
            actualValue = await Sut.GetOrAddAsync(_cacheKey, _ => { generatorWasCalled = true; return Task.FromResult(generatorReturn); }, expiration: _clock.UtcNow.Add(TimeSpan.FromSeconds(2)), token: testContextAccessor.Current.CancellationToken);
        }
        else
        {
            actualValue = await Sut.GetOrAddAsync(_cacheKey, _ => { generatorWasCalled = true; return Task.FromResult(generatorReturn); }, (CachePolicy?)null, testContextAccessor.Current.CancellationToken);
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
                .Returns(_ => Task.FromResult(expectedResponse));
        bool? actualResponse = default;
        if (expirationType == typeof(TimeSpan))
        {
            actualResponse = await Sut.SetAsync(_cacheKey, value, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);
        }
        else if (expirationType == typeof(DateTimeOffset))
        {
            actualResponse = await Sut.SetAsync(_cacheKey, value, _fixture.Create<DateTimeOffset>(), token: testContextAccessor.Current.CancellationToken);
        }
        else
        {
            actualResponse = await Sut.SetAsync(_cacheKey, value, policy: null, token: testContextAccessor.Current.CancellationToken);
        }

        actualResponse.Should().Be(expectedResponse);
        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)));
    }

    private async Task Multi_set_works_as_expected(Type expirationType)
    {
        var value = _fixture.Create<string?>();
        var expiration = _fixture.Create<TimeSpan>();
        var expectedResponse = _fixture.Create<bool>();
        _transaction.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)))
                .Returns(_ => Task.FromResult(expectedResponse));
        _transaction.StringSetAsync(_redisMultiKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)))
                .Returns(_ => Task.FromResult(expectedResponse));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(_ => Task.FromResult(expectedResponse));
        bool? actualResponse = default;
        if (expirationType == typeof(TimeSpan))
        {
            actualResponse = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);
        }
        else if (expirationType == typeof(DateTimeOffset))
        {
            actualResponse = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<DateTimeOffset>(), policy: null, token: testContextAccessor.Current.CancellationToken);
        }
        else
        {
            actualResponse = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, policy: null, token: testContextAccessor.Current.CancellationToken);
        }

        actualResponse.Should().Be(expectedResponse);
        await _transaction.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)));
        await _transaction.Received(1).StringSetAsync(_redisMultiKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)));
    }
}
