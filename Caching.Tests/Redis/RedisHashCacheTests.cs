using Microsoft.Extensions.Internal;
using Newtonsoft.Json;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using UiPath.Platform.Caching;
using UiPath.Platform.Caching.Policies;
using JsonSerializer = UiPath.Platform.Caching.SystemJsonSerializerProxy;

namespace UiPath.Platform.Caching.Tests.Redis;

public class RedisHashCacheTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    private string _prefix = default!;
    private IDatabase _database = default!;
    private ITransaction _transaction = default!;
    private ISerializerProxy _serializer = default!;
    private ISystemClock _clock = default!;
    private RedisCacheOptions _redisCacheOptions = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private IPolicyHolder _policyHolder = default!;
    private CacheKey _cacheKey = default!;
    private RedisKey _redisKey = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private IRedisKeyStrategy _redisKeyStrategy = default!;

    private RedisHashCache Sut => _fixture.Create<RedisHashCache>();

    [Fact]
    public async Task Get_data_from_cacheKey_cache()
    {
        var field = _fixture.Create<string>();
        var expected = _fixture.Create<string>();
        RedisValue redisField = field;
        _database.HashGetAsync(_redisKey, redisField, CommandFlags.PreferReplica)
            .Returns(c =>
            {
                RedisValue ret = JsonConvert.SerializeObject(expected);
                return ret;
            });

        var actual = await Sut.GetItemAsync<string>(_cacheKey, field, CancellationToken.None);
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    public async Task Get_data_key(string key)
    {
        var expected = _fixture.Create<string>();
        RedisValue redisField = key;

        Func<Task> act = async () => { await Sut.GetItemAsync<string>(_cacheKey, key, CancellationToken.None); };
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Get_data_from_redis_exception()
    {
        var field = _fixture.Create<string>();
        RedisValue redisField = field;
        _database.HashGetAsync(_redisKey, redisField, CommandFlags.PreferReplica)
            .ThrowsAsync<Exception>();

        var actual = await Sut.GetItemAsync<string>(_cacheKey, field, CancellationToken.None);
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Get_fields_data_from_cacheKey_redis()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var redisFields = fields.Select(k => (RedisValue)k).ToArray();
        var expectedValues = fields.Select(k => _fixture.Create<string>()).ToArray();
        var expected = new Dictionary<string, string?>();
        for (var i = 0; i < fields.Length; i++)
        {
            expected.Add(fields[i], expectedValues[i]);
        }

        _database.HashGetAsync(_redisKey, redisFields, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(c =>
            {
                var ret = expectedValues.Select(e => (RedisValue)JsonConvert.SerializeObject(e)).ToArray();
                return ret;
            });

        var actual = await Sut.GetAsync<string>(_cacheKey, fields, CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Get_fields_data_from_cacheKey_redis_exception()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var redisFields = fields.Select(k => (RedisValue)k).ToArray();
        var expected = fields.Select(k => _fixture.Create<string>()).ToArray();
        _database.HashGetAsync(_redisKey, redisFields, CommandFlags.PreferReplica)
            .ThrowsAsync<Exception>();

        var actual = await Sut.GetAsync<string>(_cacheKey, fields, CancellationToken.None);
        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_no_fields_returns_cacheKey()
    {
        var fields = Array.Empty<string>();
        _database.HashGetAllAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(c =>
            {
                return Array.Empty<HashEntry>();
            });

        await Sut.GetAsync<string>(_cacheKey, fields, CancellationToken.None);
        await _database.Received(1).HashGetAllAsync(_redisKey, CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task Get_all_data_from_cacheKey_redis()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var redisFields = fields.Select(k => (RedisValue)k).ToArray();
        var expected = fields.ToDictionary(k => k, k => _fixture.Create<string?>());
        _database.HashGetAllAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(c =>
            {
                var ret = expected.Select(kv => new HashEntry(kv.Key, JsonConvert.SerializeObject(kv.Value))).ToArray();
                return ret;
            });

        var actual = await Sut.GetAsync<string>(_cacheKey, CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Get_all_data_from_cacheKey_redis_exception()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var redisFields = fields.Select(k => (RedisValue)k).ToArray();
        var expected = fields.ToDictionary(k => k, k => _fixture.Create<string>());
        _database.HashGetAllAsync(_redisKey, CommandFlags.PreferReplica)
            .ThrowsAsync<Exception>();

        var actual = await Sut.GetAsync<string>(_cacheKey, CancellationToken.None);
        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_cache_entry_exception()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var redisFields = fields.Select(k => (RedisValue)k).ToArray();
        var expected = fields.ToDictionary(k => k, k => _fixture.Create<string>());
        _database.HashGetAllAsync(_redisKey, CommandFlags.PreferReplica)
            .ThrowsAsync<Exception>();

        var actual = await Sut.GetCacheEntryAsync<string>(_cacheKey, CancellationToken.None);
        actual.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_cache_entry_failed_transaction()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var redisFields = fields.Select(k => (RedisValue)k).ToArray();
        var expected = fields.ToDictionary(k => k, k => _fixture.Create<string>());
        _database.KeyExistsAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(ci => true);
        _transaction.ExecuteAsync().Returns(false);
        var actual = await Sut.GetCacheEntryAsync<string>(_cacheKey, CancellationToken.None);
        actual.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_cache_entry_for_unknown_cacheKey()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        _database.KeyExistsAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(ci => false);

        var actual = await Sut.GetCacheEntryAsync<string>(_cacheKey, CancellationToken.None);
        actual.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_cache_entry_with_metadata_v7()
    {
        _redisCacheOptions.Version = 7;
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var extendedProps = _fixture.Create<IDictionary<string, string?>>();
        var expireTime = _now.AddSeconds(1);

        var entries = expected.Select(kv => new HashEntry(kv.Key, JsonConvert.SerializeObject(kv.Value)))
            .Union(new[] { new HashEntry("_metadata_", JsonConvert.SerializeObject(extendedProps)) })
            .ToArray();
        _database.KeyExistsAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(ci => true);
        _transaction.HashGetAllAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(entries);
        _transaction.KeyExpireTimeAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns((DateTime?)expireTime.UtcDateTime);
        _transaction.ExecuteAsync().Returns(true);
        var actual = await Sut.GetCacheEntryAsync<string>(_cacheKey, CancellationToken.None);
        actual.Value.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Get_cache_entry_metadata_v6()
    {
        _redisCacheOptions.Version = 6;
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var extendedProps = _fixture.Create<IDictionary<string, string?>>();
        var expireTime = _fixture.Create<TimeSpan>();

        var entries =
            new[] { new HashEntry("_metadata_", JsonConvert.SerializeObject(extendedProps)) }
            .Union(expected.Select(kv => new HashEntry(kv.Key, JsonConvert.SerializeObject(kv.Value))))
            .ToArray();
        _database.KeyExistsAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(ci => true);
        _transaction.HashGetAllAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(entries);
        _transaction.KeyTimeToLiveAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns((TimeSpan?)expireTime);
        _transaction.ExecuteAsync().Returns(true);
        var actual = await Sut.GetCacheEntryAsync<string>(_cacheKey, CancellationToken.None);
        actual.Value.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_generator_not_called()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var redisFields = fields.Select(k => (RedisValue)k).ToArray();
        var expected = fields.ToDictionary(k => k, k => _fixture.Create<string>());
        _database.HashGetAllAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(c =>
            {
                var ret = expected.Select(kv => new HashEntry(kv.Key, JsonConvert.SerializeObject(kv.Value))).ToArray();
                return ret;
            });
        var generatorCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorCalled = true;
            return Task.FromResult(fields.ToDictionary(k => k, k => _fixture.Create<string>()) as IDictionary<string, string?>);
        };

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan?>(), CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
        generatorCalled.Should().BeFalse();
    }

    [Fact]
    public async Task GetOrAdd_generator_no_expiration()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var redisFields = fields.Select(k => (RedisValue)k).ToArray();
        var expected = fields.ToDictionary(k => k, k => _fixture.Create<string>());
        _database.HashGetAllAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(c =>
            {
                var ret = expected.Select(kv => new HashEntry(kv.Key, JsonConvert.SerializeObject(kv.Value))).ToArray();
                return ret;
            });
        var generatorCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorCalled = true;
            return Task.FromResult(fields.ToDictionary(k => k, k => _fixture.Create<string>()) as IDictionary<string, string?>);
        };

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
        generatorCalled.Should().BeFalse();
    }

    [Fact]
    public async Task GetOrAdd_generator_called()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var entries = _fixture.CreateMany<HashEntry>().ToArray();
        var redisFields = entries.Select(e => e.Name).ToArray();
        IDictionary<string, string?> expected = entries.ToDictionary(k => k.Name.ToString(), k => (string?)k.Value);
        _database.HashGetAllAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(c =>
            {
                return Array.Empty<HashEntry>();
            });
        var generatorCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorCalled = true;
            return Task.FromResult(expected);
        };
        _transaction.ExecuteAsync().Returns(true);
        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan?>(), CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
        _database.Received(1).CreateTransaction();
        await _transaction.Received(1).HashSetAsync(_redisKey, Arg.Any<HashEntry[]>(), CommandFlags.DemandMaster);
        generatorCalled.Should().BeTrue();
    }

    [Theory]
    [InlineData(HashCacheSetOption.KeyReplace)]
    [InlineData(HashCacheSetOption.HashReplace)]
    public async Task GetOrAdd_generator_called_hashCacheSetOption(HashCacheSetOption hashCacheSetOption)
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var entries = _fixture.CreateMany<HashEntry>().ToArray();
        var redisFields = entries.Select(e => e.Name).ToArray();
        IDictionary<string, string?> expected = entries.ToDictionary(k => k.Name.ToString(), k => (string?)k.Value);
        _database.HashGetAllAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(c =>
            {
                return Array.Empty<HashEntry>();
            });
        var generatorCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorCalled = true;
            return Task.FromResult(expected);
        };
        _transaction.ExecuteAsync().Returns(true);
        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _clock.UtcNow.AddDays(1), hashCacheSetOption, CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
        _database.Received().CreateTransaction();
        await _transaction.Received(1).HashSetAsync(_redisKey, Arg.Any<HashEntry[]>(), CommandFlags.DemandMaster);
        generatorCalled.Should().BeTrue();
    }

    [Fact]
    public async Task GetOrAdd_generator_called_empty_result()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var entries = _fixture.CreateMany<HashEntry>().ToArray();
        var redisFields = entries.Select(e => e.Name).ToArray();
        IDictionary<string, string?> expected = new Dictionary<string, string?>();
        _database.HashGetAllAsync(_redisKey, CommandFlags.PreferReplica)
            .Returns(c =>
            {
                return Array.Empty<HashEntry>();
            });
        var generatorCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorCalled = true;
            return Task.FromResult(expected);
        };
        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan?>(), CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
        _database.Received(0).CreateTransaction();
        await _transaction.Received(0).HashSetAsync(_redisKey, Arg.Any<HashEntry[]>(), CommandFlags.DemandMaster);
        await _database.Received(1).KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster);
        generatorCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_null_cacheKey_throws_exception()
    {
        Func<Task> act = async () => { await Sut.ContainsAsync<string>(CacheKey.Null); };
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Contains_works_as_expected()
    {
        var fieldsCalled = false;
        _database.KeyExistsAsync(Arg.Any<RedisKey>(), CommandFlags.PreferReplica)
            .Returns(c =>
            {
                fieldsCalled = true;
                return true;
            });

        var actual = await Sut.ContainsAsync<string>(_cacheKey, CancellationToken.None);
        fieldsCalled.Should().BeTrue();
        actual.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_redis_exception()
    {
        _database.KeyExistsAsync(_redisKey, CommandFlags.PreferReplica)
            .ThrowsAsync<Exception>();

        var actual = await Sut.ContainsAsync<string>(_cacheKey, CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public async Task Refresh_works_as_expected(int? expirationMinutes)
    {
        TimeSpan? expiration = expirationMinutes.HasValue ? TimeSpan.FromMinutes(expirationMinutes.Value) : null;
        var fieldsCalled = false;
        DateTime actualExpiration = default;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .Returns(c =>
            {
                fieldsCalled = true;
                actualExpiration = ((DateTime?)c[1]).GetValueOrDefault();
                return _fixture.Create<bool>();
            });

        await Sut.RefreshAsync<string>(_cacheKey, expiration, CancellationToken.None);
        fieldsCalled.Should().BeTrue();
        var expectedTime = expirationMinutes.HasValue
            ? _clock.UtcNow.AddMinutes(expirationMinutes.Value).UtcDateTime
            : _clock.UtcNow.Add(_redisCacheOptions.DefaultExpiration ?? TimeSpan.MinValue).UtcDateTime;
        actualExpiration.Should().Be(expectedTime);
    }

    [Fact]
    public async Task Refresh_default_expiration()
    {
        var fieldsCalled = false;
        DateTime actualExpiration = default;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .Returns(c =>
            {
                fieldsCalled = true;
                actualExpiration = ((DateTime?)c[1]).GetValueOrDefault();
                return _fixture.Create<bool>();
            });

        await Sut.RefreshAsync<string>(_cacheKey, CancellationToken.None);
        fieldsCalled.Should().BeTrue();
        var expectedTime = _clock.UtcNow.Add(_redisCacheOptions.DefaultExpiration ?? TimeSpan.MinValue).UtcDateTime;
        actualExpiration.Should().Be(expectedTime);
    }

    [Fact]
    public async Task Refresh_redis_exception_timespan()
    {
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .ThrowsAsync<Exception>();

        var actual = await Sut.RefreshAsync<string>(_cacheKey, _fixture.Create<TimeSpan?>());
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_redis_exception_datetime()
    {
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .ThrowsAsync<Exception>();

        var actual = await Sut.RefreshAsync<string>(_cacheKey, _fixture.Create<DateTimeOffset?>(), CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_redis_exception_datetime_max()
    {
        _database.KeyPersistAsync(_redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .ThrowsAsync<Exception>();

        var actual = await Sut.RefreshAsync<string>(_cacheKey, DateTimeOffset.MaxValue);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_HashCacheEntryOptions_expired()
    {
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .ThrowsAsync<Exception>();
        var options = new HashCacheEntryOptions(_now.Subtract(TimeSpan.FromMilliseconds(1)), null, null);
        var actual = await Sut.RefreshAsync<string>(_cacheKey, options, CancellationToken.None);
        await _database.Received(1).KeyDeleteAsync(_redisKey, Arg.Any<CommandFlags>());
        await _transaction.DidNotReceive().ExecuteAsync();
    }

    [Fact]
    public async Task Refresh_HashCacheEntryOptions_with_extended_props()
    {
        var metadata = _fixture.Create<IDictionary<string, string?>>();
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .ThrowsAsync<Exception>();
        var options = new HashCacheEntryOptions(default, _fixture.Create<TimeSpan>(), metadata);
        var actual = await Sut.RefreshAsync<string>(_cacheKey, options, CancellationToken.None);
        await _database.DidNotReceive().KeyDeleteAsync(_redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).HashSetAsync(_redisKey, Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>());
        await _transaction.Received(1).KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>());
        await _transaction.Received(1).ExecuteAsync();
    }

    [Fact]
    public async Task Refresh_HashCacheEntryOptions_no_extended_props()
    {
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .ThrowsAsync<Exception>();
        var options = new HashCacheEntryOptions(default, _fixture.Create<TimeSpan>(), default);
        var actual = await Sut.RefreshAsync<string>(_cacheKey, options, CancellationToken.None);
        await _database.DidNotReceive().KeyDeleteAsync(_redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).HashDeleteAsync(_redisKey, new RedisValue("_metadata_"), Arg.Any<CommandFlags>());
        await _transaction.Received(1).KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>());
        await _transaction.Received(1).ExecuteAsync();
    }

    [Fact]
    public async Task Refresh_HashCacheEntryOptions_with_extended_props_no_default_expiration()
    {
        _redisCacheOptions.DefaultExpiration = null;
        var metadata = _fixture.Create<IDictionary<string, string?>>();
        var options = new HashCacheEntryOptions(default, default, metadata);
        var actual = await Sut.RefreshAsync<string>(_cacheKey, options, CancellationToken.None);
        await _database.DidNotReceive().KeyDeleteAsync(_redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).HashSetAsync(_redisKey, Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>());
        await _transaction.Received(1).KeyPersistAsync(_redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).ExecuteAsync();
    }

    [Fact]
    public async Task Refresh_HashCacheEntryOptions_transaction_fail()
    {
        _redisCacheOptions.DefaultExpiration = null;
        var metadata = _fixture.Create<IDictionary<string, string?>>();
        var options = new HashCacheEntryOptions(default, default, metadata);
        _transaction.ExecuteAsync().Returns(false);
        var actual = await Sut.RefreshAsync<string>(_cacheKey, options, CancellationToken.None);
        await _database.DidNotReceive().KeyDeleteAsync(_redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).HashSetAsync(_redisKey, Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>());
        await _transaction.Received(1).KeyPersistAsync(_redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).ExecuteAsync();
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_HashCacheEntryOptions_transaction_exception()
    {
        _redisCacheOptions.DefaultExpiration = null;
        var metadata = _fixture.Create<IDictionary<string, string?>>();
        var options = new HashCacheEntryOptions(default, default, metadata);
        _transaction.ExecuteAsync().ThrowsAsync(new Exception());
        var actual = await Sut.RefreshAsync<string>(_cacheKey, options, CancellationToken.None);
        await _database.DidNotReceive().KeyDeleteAsync(_redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).HashSetAsync(_redisKey, Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>());
        await _transaction.Received(1).KeyPersistAsync(_redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).ExecuteAsync();
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_works_as_expected()
    {
        var field = _fixture.Create<string>().ToLowerInvariant();
        RedisValue redisField = field;
        var actionCalled = false;
        var expected = _fixture.Create<bool>();
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
            .Returns(c =>
            {
                actionCalled = true;
                return expected;
            });

        var actual = await Sut.RemoveAsync<string>(_cacheKey, CancellationToken.None);
        actionCalled.Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Fact]
    public async Task Remove_redis_exception()
    {
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
            .ThrowsAsync<Exception>();

        var actual = await Sut.RemoveAsync<string>(_cacheKey, CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Remove_cacheKey_validates_input(string? cacheKey)
    {
        Func<Task> act = async () => { await Sut.RemoveAsync<string>(cacheKey); };
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Remove_cacheKey_works_as_expected()
    {
        var actionCalled = false;
        var expected = _fixture.Create<bool>();
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
            .Returns(c =>
            {
                actionCalled = true;
                return expected;
            });

        var actual = await Sut.RemoveAsync<string>(_cacheKey, CancellationToken.None);
        actionCalled.Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Fact]
    public async Task Remove_cacheKey_redis_exception()
    {
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
            .ThrowsAsync<Exception>();

        var actual = await Sut.RemoveAsync<string>(_cacheKey, CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Set_works_as_expected(bool transactionSuccess)
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var entries = _fixture.CreateMany<HashEntry>().ToArray();
        var expiration = _clock.UtcNow.AddHours(5);
        IDictionary<string, string?> values = entries.ToDictionary(k => k.Name.ToString(), k => (string?)k.Value);
        IDictionary<string, string?> expected = entries.ToDictionary(k => k.Name.ToString(), k => (string?)k.Value);
        _transaction.ExecuteAsync().Returns(transactionSuccess);
        var actual = await Sut.SetAsync(_cacheKey, values, expiration, CancellationToken.None);
        actual.Should().Be(transactionSuccess);
        _database.Received(1).CreateTransaction();
        await _transaction.Received(1).HashSetAsync(_redisKey, Arg.Any<HashEntry[]>(), CommandFlags.DemandMaster);
        await _transaction.Received(1).KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task Set_no_expiration()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var entries = _fixture.CreateMany<HashEntry>().ToArray();
        IDictionary<string, string?> values = entries.ToDictionary(k => k.Name.ToString(), k => (string?)k.Value);
        IDictionary<string, string?> expected = entries.ToDictionary(k => k.Name.ToString(), k => (string?)k.Value);
        _transaction.ExecuteAsync().Returns(true);
        var actual = await Sut.SetAsync(_cacheKey, values, CancellationToken.None);
        actual.Should().Be(true);
        _database.Received(1).CreateTransaction();
        await _transaction.Received(1).HashSetAsync(_redisKey, Arg.Any<HashEntry[]>(), CommandFlags.DemandMaster);
        await _transaction.Received(1).KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task Set_HashCacheEntryOptions()
    {
        var values = _fixture.Create<IDictionary<string, string?>>();
        var metadata = _fixture.Create<IDictionary<string, string?>>();
        var options = new HashCacheEntryOptions(_now.AddMilliseconds(1), default, metadata);
        await Sut.SetAsync(_cacheKey, values, options, CancellationToken.None);
        _database.Received(1).CreateTransaction();
    }

    [Fact]
    public async Task Set_HashReplaceSetOption()
    {
        var values = _fixture.Create<IDictionary<string, string?>>();
        var metadata = _fixture.Create<IDictionary<string, string?>>();
        var options = new HashCacheEntryOptions(_now.AddMilliseconds(1), default, metadata, HashCacheSetOption.HashReplace);
        await Sut.SetAsync(_cacheKey, values, options, CancellationToken.None);
        _database.Received(1).CreateTransaction();
        await _database.DidNotReceive().KeyDeleteAsync(_redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).HashSetAsync(_redisKey, Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>());
        await _transaction.Received(1).ExecuteAsync();
    }

    [Fact]
    public async Task Set_empty_values()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var values = new Dictionary<string, string?>();
        _transaction.ExecuteAsync().Returns(true);
        var actionCalled = false;
        var expected = _fixture.Create<bool>();
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
            .Returns(c =>
            {
                actionCalled = true;
                return expected;
            });

        var actual = await Sut.SetAsync(_cacheKey, values, _fixture.Create<TimeSpan?>(), CancellationToken.None);
        actionCalled.Should().BeTrue();
        actual.Should().Be(expected);
        _database.Received(0).CreateTransaction();
        await _transaction.Received(0).HashSetAsync(_redisKey, Arg.Any<HashEntry[]>(), CommandFlags.DemandMaster);
        await _transaction.Received(0).KeyExpireAsync(_redisKey, Arg.Any<TimeSpan?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task Set_no_fields()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        IDictionary<string, string?> values = null!;
        Func<Task> act = async () => await Sut.SetAsync(_cacheKey, values, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Set_with_special_key()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var values = _fixture.Create<IDictionary<string, string?>>();
        values.Add("_metadata_", _fixture.Create<string>());
        Func<Task> act = async () => await Sut.SetAsync(_cacheKey, values, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Set_redis_exception()
    {
        var fields = _fixture.CreateMany<string>().ToArray();
        var entries = _fixture.CreateMany<HashEntry>().ToArray();
        IDictionary<string, string?> values = entries.ToDictionary(k => k.Name.ToString(), k => (string?)k.Value);
        _transaction.ExecuteAsync().ThrowsAsync<Exception>();
        var actual = await Sut.SetAsync(_cacheKey, values, _fixture.Create<TimeSpan?>(), CancellationToken.None);
        actual.Should().BeFalse();
        _database.Received(1).CreateTransaction();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Unknown_cacheKey_v7()
    {
        _redisCacheOptions.Version = 7;
        _database.KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(default(DateTime?));
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, CancellationToken.None);
        await _database.Received(1).KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_ExpireTime_throw_exception_v7()
    {
        _redisCacheOptions.Version = 7;
        _database.KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>())
            .ThrowsAsync(new Exception());
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, CancellationToken.None);
        await _database.Received(1).KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Known_cacheKey_v7()
    {
        _redisCacheOptions.Version = 7;
        DateTimeOffset? expected = _fixture.Create<DateTimeOffset>();
        _database.KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(c => (DateTime?)expected.GetValueOrDefault().UtcDateTime);
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, CancellationToken.None);
        await _database.Received(1).KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().Be(expected.GetValueOrDefault());
    }

    [Fact]
    public async Task Read_ExpireTime_For_Known_cacheKey_v6()
    {
        var wasCalled = false;
        _redisCacheOptions.Version = 6;
        TimeSpan? expected = _fixture.Create<TimeSpan>();
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                wasCalled = true;
                return expected;
            });
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, CancellationToken.None);
        wasCalled.Should().BeTrue();
        actual.Should().NotBeNull();
    }
    [Fact]
    public async Task Read_ExpireTime_For_Known_cacheKey_v6_not_default_expiration()
    {
        var wasCalled = false;
        _redisCacheOptions.Version = 6;
        _redisCacheOptions.DefaultExpiration = null;
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                wasCalled = true;
                return default(TimeSpan?);
            });
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, CancellationToken.None);
        wasCalled.Should().BeTrue();
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_TimeToLive_For_Unknown_cacheKey()
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
    public async Task SetMetadata_works_for_unknown_cacheKey()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        _database.KeyExistsAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(false);

        var response = await Sut.SetMetadataAsync<string>(_cacheKey, expected, CancellationToken.None);
        response.Should().BeFalse();
        await _database.Received(1).KeyExistsAsync(_redisKey, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().HashSetAsync(_redisKey, Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>());
        await _database.DidNotReceive().HashDeleteAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SetMetadata_works_for_known_cacheKey()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        _database.KeyExistsAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(true);

        await Sut.SetMetadataAsync<string>(_cacheKey, expected, CancellationToken.None);
        await _database.Received().KeyExistsAsync(_redisKey, Arg.Any<CommandFlags>());
        await _database.Received().HashSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
        await _database.DidNotReceive().HashDeleteAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SetMetadata_works_with_no_props()
    {
        _database.KeyExistsAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(true);

        await Sut.SetMetadataAsync<string>(_cacheKey, new Dictionary<string, string?>(), CancellationToken.None);
        await _database.Received().KeyExistsAsync(_redisKey, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().HashSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
        await _database.Received().HashDeleteAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SetMetadata_throw_exception()
    {
        _database.KeyExistsAsync(_redisKey, Arg.Any<CommandFlags>())
            .ThrowsAsync(new Exception());

        var actual = await Sut.SetMetadataAsync<string>(_cacheKey, new Dictionary<string, string?>(), CancellationToken.None);
        await _database.Received().KeyExistsAsync(_redisKey, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().HashSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
        await _database.DidNotReceive().HashDeleteAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task GetMetadata_works_as_expected()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        _database.KeyExistsAsync(_redisKey, Arg.Any<CommandFlags>())
            .ThrowsAsync(new Exception());
        _database.HashGetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(new RedisValue(JsonConvert.SerializeObject(expected)));
        var actual = await Sut.GetMetadataAsync<string>(_cacheKey, CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
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
        _prefix = _fixture.Create<string>();
        _cacheKey = _fixture.Create<string>();
        _redisKey = string.Join(':', _prefix, RedisTypePrefixes.Hash, _cacheKey).ToLowerInvariant();

        _database = _fixture.Freeze<IDatabase>();
        _transaction = _fixture.Freeze<ITransaction>();
        _clock = _fixture.Freeze<ISystemClock>();
        _clock.UtcNow.Returns(c => _now);
        _policyHolder = _fixture.Freeze<IPolicyHolder>();
        var noOpExecutor = new NoOpExecutor();
        _policyHolder.Read.Returns(noOpExecutor);
        _policyHolder.Write.Returns(noOpExecutor);
        _database.CreateTransaction().Returns(_transaction);
        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        var redisKeyStrategyFactory = _fixture.Create<IRedisKeyStrategyFactory>();
        _redisKeyStrategy = _fixture.Create<IRedisKeyStrategy>();
        _redisKeyStrategy.GetRedisKey(_cacheKey).Returns(_redisKey);
        redisKeyStrategyFactory.Create(Arg.Any<CacheOptions>(), Arg.Any<Type>())
            .Returns(_redisKeyStrategy);

        _redisCacheOptions = new RedisCacheOptions
        {
            DefaultExpiration = TimeSpan.FromSeconds(Random.Shared.Next(1, 100)),
            Clock = _clock,
            EntryFactory = new TestCacheEntryFactory(),
            CacheKeyStrategy = _cacheKeyStrategy,
            RedisKeyStrategyFactory = redisKeyStrategyFactory
        };
        _serializer = new JsonSerializer();
        _fixture.Inject(_serializer);
        var opt = Options.Create(_redisCacheOptions);
        _fixture.Inject(opt);
        return Task.CompletedTask;
    }
}
