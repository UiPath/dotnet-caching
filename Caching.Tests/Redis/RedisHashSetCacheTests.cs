using Microsoft.Extensions.Internal;
using Newtonsoft.Json;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using JsonSerializer = UiPath.Platform.Caching.Redis.SystemJsonSerializerProxy;

namespace UiPath.Platform.Caching.Tests.Redis;

public class RedisHashSetCacheTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    private IDatabase _database = default!;
    private ITransaction _transaction = default!;
    private ISerializerProxy _serializer = default!;
    private ISystemClock _clock = default!;
    private RedisCacheOptions _redisCacheOptions = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private IPolicyHolder _policyHolder = default!;

    private RedisHashSetCache Sut => _fixture.Create<RedisHashSetCache>();

    [Fact]
    public async Task Get_data_from_region_cache()
    {
        var key = _fixture.Create<string>();
        var expected = _fixture.Create<string>();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        RedisValue redisField = key;
        _database.HashGetAsync(redisKey, redisField, CommandFlags.PreferReplica)
            .Returns(c =>
            {
                RedisValue ret = JsonConvert.SerializeObject(expected);
                return ret;
            });

        var actual = await Sut.GetItemAsync<string>(region, key, CancellationToken.None);
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Get_data_key(string key)
    {
        var expected = _fixture.Create<string>();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        RedisValue redisField = key;

        Func<Task> act = async () => { await Sut.GetItemAsync<string>(region, key, CancellationToken.None); };
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Get_data_from_redis_exception()
    {
        var key = _fixture.Create<string>();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Concat(_redisCacheOptions.InstanceName, region).ToLowerInvariant();
        RedisValue redisField = key;
        _database.HashGetAsync(redisKey, redisField, CommandFlags.PreferReplica)
            .ThrowsAsync<Exception>();

        var actual = await Sut.GetItemAsync<string>(region, key, CancellationToken.None);
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Get_keys_data_from_region_redis()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var redisFields = keys.Select(k => (RedisValue)k).ToArray();
        var expectedValues = keys.Select(k => _fixture.Create<string>()).ToArray();
        var expected = new Dictionary<string, string?>();
        for (var i = 0; i < keys.Length; i++)
        {
            expected.Add(keys[i], expectedValues[i]);
        }

        _database.HashGetAsync(redisKey, redisFields, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(c =>
            {
                var ret = expectedValues.Select(e => (RedisValue)JsonConvert.SerializeObject(e)).ToArray();
                return ret;
            });

        var actual = await Sut.GetAsync<string>(region, keys, CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Get_keys_data_from_region_redis_exception()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var redisFields = keys.Select(k => (RedisValue)k).ToArray();
        var expected = keys.Select(k => _fixture.Create<string>()).ToArray();
        _database.HashGetAsync(redisKey, redisFields, CommandFlags.PreferReplica)
            .ThrowsAsync<Exception>();

        var actual = await Sut.GetAsync<string>(region, keys, CancellationToken.None);
        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_no_keys_returns_region()
    {
        var keys = Array.Empty<string>();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        _database.HashGetAllAsync(redisKey, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(c =>
            {
                return Array.Empty<HashEntry>();
            });

        await Sut.GetAsync<string>(region, keys, CancellationToken.None);
        await _database.Received(1).HashGetAllAsync(redisKey, CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task Get_all_data_from_region_redis()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var redisFields = keys.Select(k => (RedisValue)k).ToArray();
        var expected = keys.ToDictionary(k => k, k => _fixture.Create<string?>());
        _database.HashGetAllAsync(redisKey, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(c =>
            {
                var ret = expected.Select(kv => new HashEntry(kv.Key, JsonConvert.SerializeObject(kv.Value))).ToArray();
                return ret;
            });

        var actual = await Sut.GetAsync<string>(region, CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Get_all_data_from_region_redis_exception()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var redisFields = keys.Select(k => (RedisValue)k).ToArray();
        var expected = keys.ToDictionary(k => k, k => _fixture.Create<string>());
        _database.HashGetAllAsync(redisKey, CommandFlags.PreferReplica)
            .ThrowsAsync<Exception>();

        var actual = await Sut.GetAsync<string>(region, CancellationToken.None);
        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_cache_entry_exception()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var redisFields = keys.Select(k => (RedisValue)k).ToArray();
        var expected = keys.ToDictionary(k => k, k => _fixture.Create<string>());
        _database.HashGetAllAsync(redisKey, CommandFlags.PreferReplica)
            .ThrowsAsync<Exception>();

        var actual = await Sut.GetCacheEntryAsync<string>(region, CancellationToken.None);
        actual.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_cache_entry_failed_transaction()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var redisFields = keys.Select(k => (RedisValue)k).ToArray();
        var expected = keys.ToDictionary(k => k, k => _fixture.Create<string>());
        _database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(ci => true);
        _transaction.ExecuteAsync().Returns(false);
        var actual = await Sut.GetCacheEntryAsync<string>(region, CancellationToken.None);
        actual.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_cache_entry_for_unknown_region()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        _database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(ci => false);

        var actual = await Sut.GetCacheEntryAsync<string>(region, CancellationToken.None);
        actual.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_cache_entry_with_extended_properties_v7()
    {
        _redisCacheOptions.Version = 7;
        var expected = _fixture.Create<IDictionary<string, string?>>();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var extendedProps = _fixture.Create<IDictionary<string, string?>>();
        var expireTime = _now.AddSeconds(1);

        var entries = expected.Select(kv => new HashEntry(kv.Key, JsonConvert.SerializeObject(kv.Value)))
            .Union(new[] { new HashEntry("_extended_properties_", JsonConvert.SerializeObject(extendedProps)) })
            .ToArray();
        _database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(ci => true);
        _transaction.HashGetAllAsync(redisKey, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(entries);
        _transaction.KeyExpireTimeAsync(redisKey, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs((DateTime?)expireTime.UtcDateTime);
        _transaction.ExecuteAsync().Returns(true);
        var actual = await Sut.GetCacheEntryAsync<string>(region, CancellationToken.None);
        actual.Value.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Get_cache_entry_with_extended_properties_v6()
    {
        _redisCacheOptions.Version = 6;
        var expected = _fixture.Create<IDictionary<string, string?>>();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var extendedProps = _fixture.Create<IDictionary<string, string?>>();
        var expireTime = _fixture.Create<TimeSpan>();

        var entries =
            new[] { new HashEntry("_extended_properties_", JsonConvert.SerializeObject(extendedProps)) }
            .Union(expected.Select(kv => new HashEntry(kv.Key, JsonConvert.SerializeObject(kv.Value))))
            .ToArray();
        _database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(ci => true);
        _transaction.HashGetAllAsync(redisKey, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(entries);
        _transaction.KeyTimeToLiveAsync(redisKey, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs((TimeSpan?)expireTime);
        _transaction.ExecuteAsync().Returns(true);
        var actual = await Sut.GetCacheEntryAsync<string>(region, CancellationToken.None);
        actual.Value.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_generator_not_called()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var redisFields = keys.Select(k => (RedisValue)k).ToArray();
        var expected = keys.ToDictionary(k => k, k => _fixture.Create<string>());
        _database.HashGetAllAsync(redisKey, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(c =>
            {
                var ret = expected.Select(kv => new HashEntry(kv.Key, JsonConvert.SerializeObject(kv.Value))).ToArray();
                return ret;
            });
        var generatorCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorCalled = true;
            return Task.FromResult(keys.ToDictionary(k => k, k => _fixture.Create<string>()) as IDictionary<string, string?>);
        };

        var actual = await Sut.GetOrAddAsync(region, generator, _fixture.Create<TimeSpan?>(), CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
        generatorCalled.Should().BeFalse();
    }

    [Fact]
    public async Task GetOrAdd_generator_no_expiration()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var redisFields = keys.Select(k => (RedisValue)k).ToArray();
        var expected = keys.ToDictionary(k => k, k => _fixture.Create<string>());
        _database.HashGetAllAsync(redisKey, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(c =>
            {
                var ret = expected.Select(kv => new HashEntry(kv.Key, JsonConvert.SerializeObject(kv.Value))).ToArray();
                return ret;
            });
        var generatorCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorCalled = true;
            return Task.FromResult(keys.ToDictionary(k => k, k => _fixture.Create<string>()) as IDictionary<string, string?>);
        };

        var actual = await Sut.GetOrAddAsync(region, generator, CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
        generatorCalled.Should().BeFalse();
    }

    [Fact]
    public async Task GetOrAdd_generator_called()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var entries = _fixture.CreateMany<HashEntry>().ToArray();
        var redisFields = entries.Select(e => e.Name).ToArray();
        IDictionary<string, string?> expected = entries.ToDictionary(k => k.Name.ToString(), k => (string?)k.Value);
        _database.HashGetAllAsync(redisKey, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(c =>
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
        var actual = await Sut.GetOrAddAsync(region, generator, _fixture.Create<TimeSpan?>(), CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
        _database.Received(1).CreateTransaction();
        await _transaction.Received(1).HashSetAsync(redisKey, Arg.Any<HashEntry[]>(), CommandFlags.DemandMaster);
        generatorCalled.Should().BeTrue();
    }

    [Fact]
    public async Task GetOrAdd_generator_called_empty_result()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var entries = _fixture.CreateMany<HashEntry>().ToArray();
        var redisFields = entries.Select(e => e.Name).ToArray();
        IDictionary<string, string?> expected = new Dictionary<string, string?>();
        _database.HashGetAllAsync(redisKey, CommandFlags.PreferReplica)
            .ReturnsForAnyArgs(c =>
            {
                return Array.Empty<HashEntry>();
            });
        var generatorCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorCalled = true;
            return Task.FromResult(expected);
        };
        var actual = await Sut.GetOrAddAsync(region, generator, _fixture.Create<TimeSpan?>(), CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
        _database.Received(0).CreateTransaction();
        await _transaction.Received(0).HashSetAsync(redisKey, Arg.Any<HashEntry[]>(), CommandFlags.DemandMaster);
        await _database.Received(1).KeyDeleteAsync(redisKey, CommandFlags.DemandMaster);
        generatorCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_null_region_throws_exception()
    {
        Func<Task> act = async () => { await Sut.ContainsAsync(Region.Null); };
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Contains_works_as_expected()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var keysCalled = false;
        _database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica)
            .Returns(c =>
            {
                keysCalled = true;
                return true;
            });

        var actual = await Sut.ContainsAsync(region, CancellationToken.None);
        keysCalled.Should().BeTrue();
        actual.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_redis_exception()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        _database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica)
            .ThrowsAsync<Exception>();

        var actual = await Sut.ContainsAsync(region, CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public async Task Refresh_works_as_expected(int? expirationMinutes)
    {
        TimeSpan? expiration = expirationMinutes.HasValue ? TimeSpan.FromMinutes(expirationMinutes.Value) : null;
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var keysCalled = false;
        DateTime actualExpiration = default;
        _database.KeyExpireAsync(redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .Returns(c =>
            {
                keysCalled = true;
                actualExpiration = ((DateTime?)c[1]).GetValueOrDefault();
                return _fixture.Create<bool>();
            });

        await Sut.RefreshAsync<string>(region, expiration, CancellationToken.None);
        keysCalled.Should().BeTrue();
        var expectedTime = expirationMinutes.HasValue
            ? _clock.UtcNow.AddMinutes(expirationMinutes.Value).UtcDateTime
            : _clock.UtcNow.Add(_redisCacheOptions.DefaultExpiration ?? TimeSpan.MinValue).UtcDateTime;
        actualExpiration.Should().Be(expectedTime);
    }

    [Fact]
    public async Task Refresh_default_expiration()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var keysCalled = false;
        DateTime actualExpiration = default;
        _database.KeyExpireAsync(redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .Returns(c =>
            {
                keysCalled = true;
                actualExpiration = ((DateTime?)c[1]).GetValueOrDefault();
                return _fixture.Create<bool>();
            });

        await Sut.RefreshAsync<string>(region, CancellationToken.None);
        keysCalled.Should().BeTrue();
        var expectedTime = _clock.UtcNow.Add(_redisCacheOptions.DefaultExpiration ?? TimeSpan.MinValue).UtcDateTime;
        actualExpiration.Should().Be(expectedTime);
    }

    [Fact]
    public async Task Refresh_redis_exception_timespan()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        _database.KeyExpireAsync(redisKey, Arg.Any<TimeSpan?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .ThrowsAsync<Exception>();

        await Sut.RefreshAsync<string>(region, _fixture.Create<TimeSpan?>());
    }

    [Fact]
    public async Task Refresh_redis_exception_datetime()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        _database.KeyExpireAsync(redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .ThrowsAsync<Exception>();

        var actual = await Sut.RefreshAsync<string>(region, _fixture.Create<DateTimeOffset?>(), CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_RegionCacheEntryOptions_expired()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        _database.KeyExpireAsync(redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .ThrowsAsync<Exception>();
        var options = new RegionCacheEntryOptions(_now.Subtract(TimeSpan.FromMilliseconds(1)), null, null);
        var actual = await Sut.RefreshAsync<string>(region, options, CancellationToken.None);
        await _database.Received(1).KeyDeleteAsync(redisKey, Arg.Any<CommandFlags>());
        await _transaction.DidNotReceive().ExecuteAsync();
    }

    [Fact]
    public async Task Refresh_RegionCacheEntryOptions_with_extended_props()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var extendedProperties = _fixture.Create<IDictionary<string, string?>>();
        _database.KeyExpireAsync(redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .ThrowsAsync<Exception>();
        var options = new RegionCacheEntryOptions(default, _fixture.Create<TimeSpan>(), extendedProperties);
        var actual = await Sut.RefreshAsync<string>(region, options, CancellationToken.None);
        await _database.DidNotReceive().KeyDeleteAsync(redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).HashSetAsync(redisKey, Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>());
        await _transaction.Received(1).KeyExpireAsync(redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>());
        await _transaction.Received(1).ExecuteAsync();
    }

    [Fact]
    public async Task Refresh_RegionCacheEntryOptions_no_extended_props()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        _database.KeyExpireAsync(redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget)
            .ThrowsAsync<Exception>();
        var options = new RegionCacheEntryOptions(default, _fixture.Create<TimeSpan>(), default);
        var actual = await Sut.RefreshAsync<string>(region, options, CancellationToken.None);
        await _database.DidNotReceive().KeyDeleteAsync(redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).HashDeleteAsync(redisKey, new RedisValue("_extended_properties_"), Arg.Any<CommandFlags>());
        await _transaction.Received(1).KeyExpireAsync(redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>());
        await _transaction.Received(1).ExecuteAsync();
    }

    [Fact]
    public async Task Refresh_RegionCacheEntryOptions_with_extended_props_no_default_expiration()
    {
        _redisCacheOptions.DefaultExpiration = null;
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var extendedProperties = _fixture.Create<IDictionary<string, string?>>();
        var options = new RegionCacheEntryOptions(default, default, extendedProperties);
        var actual = await Sut.RefreshAsync<string>(region, options, CancellationToken.None);
        await _database.DidNotReceive().KeyDeleteAsync(redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).HashSetAsync(redisKey, Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>());
        await _transaction.Received(1).KeyPersistAsync(redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).ExecuteAsync();
    }

    [Fact]
    public async Task Refresh_RegionCacheEntryOptions_tranaction_fail()
    {
        _redisCacheOptions.DefaultExpiration = null;
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var extendedProperties = _fixture.Create<IDictionary<string, string?>>();
        var options = new RegionCacheEntryOptions(default, default, extendedProperties);
        _transaction.ExecuteAsync().Returns(false);
        var actual = await Sut.RefreshAsync<string>(region, options, CancellationToken.None);
        await _database.DidNotReceive().KeyDeleteAsync(redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).HashSetAsync(redisKey, Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>());
        await _transaction.Received(1).KeyPersistAsync(redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).ExecuteAsync();
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_RegionCacheEntryOptions_tranaction_exception()
    {
        _redisCacheOptions.DefaultExpiration = null;
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var extendedProperties = _fixture.Create<IDictionary<string, string?>>();
        var options = new RegionCacheEntryOptions(default, default, extendedProperties);
        _transaction.ExecuteAsync().Throws(new Exception());
        var actual = await Sut.RefreshAsync<string>(region, options, CancellationToken.None);
        await _database.DidNotReceive().KeyDeleteAsync(redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).HashSetAsync(redisKey, Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>());
        await _transaction.Received(1).KeyPersistAsync(redisKey, Arg.Any<CommandFlags>());
        await _transaction.Received(1).ExecuteAsync();
        actual.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "k1")]
    [InlineData("r1", null)]
    [InlineData("", null)]
    [InlineData("r1", "")]
    public async Task Remove_validates_input(string region, string key)
    {
        Func<Task> act = async () => { await Sut.RemoveAsync<string>(region, key); };
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Remove_works_as_expected()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var key = _fixture.Create<string>().ToLowerInvariant();
        RedisValue redisField = key;
        var actionCalled = false;
        var expected = _fixture.Create<bool>();
        _database.HashDeleteAsync(redisKey, redisField, CommandFlags.DemandMaster)
            .Returns(c =>
            {
                actionCalled = true;
                return expected;
            });

        var actual = await Sut.RemoveAsync<string>(region, key, CancellationToken.None);
        actionCalled.Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Fact]
    public async Task Remove_redis_exception()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var key = _fixture.Create<string>().ToLowerInvariant();
        RedisValue redisField = key;
        _database.HashDeleteAsync(redisKey, redisField, CommandFlags.DemandMaster)
            .ThrowsAsync<Exception>();

        var actual = await Sut.RemoveAsync<string>(region, key, CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Remove_region_validates_input(string region)
    {
        Func<Task> act = async () => { await Sut.RemoveAsync<string>(region); };
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Remove_region_works_as_expected()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var actionCalled = false;
        var expected = _fixture.Create<bool>();
        _database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster)
            .Returns(c =>
            {
                actionCalled = true;
                return expected;
            });

        var actual = await Sut.RemoveAsync<string>(region, CancellationToken.None);
        actionCalled.Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Fact]
    public async Task Remove_region_redis_exception()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        _database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster)
            .ThrowsAsync<Exception>();

        var actual = await Sut.RemoveAsync<string>(region, CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Set_works_as_expected(bool transactionSuccess)
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var entries = _fixture.CreateMany<HashEntry>().ToArray();
        var expiration = _clock.UtcNow.AddHours(5);
        IDictionary<string, string?> values = entries.ToDictionary(k => k.Name.ToString(), k => (string?)k.Value);
        IDictionary<string, string?> expected = entries.ToDictionary(k => k.Name.ToString(), k => (string?)k.Value);
        _transaction.ExecuteAsync().Returns(transactionSuccess);
        var actual = await Sut.SetAsync(region, values, expiration, CancellationToken.None);
        actual.Should().Be(transactionSuccess);
        _database.Received(1).CreateTransaction();
        await _transaction.Received(1).HashSetAsync(redisKey, Arg.Any<HashEntry[]>(), CommandFlags.DemandMaster);
        await _transaction.Received(1).KeyExpireAsync(redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task Set_no_expiration()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var entries = _fixture.CreateMany<HashEntry>().ToArray();
        IDictionary<string, string?> values = entries.ToDictionary(k => k.Name.ToString(), k => (string?)k.Value);
        IDictionary<string, string?> expected = entries.ToDictionary(k => k.Name.ToString(), k => (string?)k.Value);
        _transaction.ExecuteAsync().Returns(true);
        var actual = await Sut.SetAsync(region, values, CancellationToken.None);
        actual.Should().Be(true);
        _database.Received(1).CreateTransaction();
        await _transaction.Received(1).HashSetAsync(redisKey, Arg.Any<HashEntry[]>(), CommandFlags.DemandMaster);
        await _transaction.Received(1).KeyExpireAsync(redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task Set_RegionCacheEntryOptions()
    {
        Region region = _fixture.Create<string>();
        var values = _fixture.Create<IDictionary<string, string?>>();
        var extendedProperties = _fixture.Create<IDictionary<string, string?>>();
        var options = new RegionCacheEntryOptions(_now.AddMilliseconds(1), default, extendedProperties);
        await Sut.SetAsync(region, values, options, CancellationToken.None);
        _database.Received(1).CreateTransaction();
    }

    [Fact]
    public async Task Set_empty_values()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var values = new Dictionary<string, string?>();
        _transaction.ExecuteAsync().Returns(true);
        var actionCalled = false;
        var expected = _fixture.Create<bool>();
        _database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster)
            .Returns(c =>
            {
                actionCalled = true;
                return expected;
            });

        var actual = await Sut.SetAsync(region, values, _fixture.Create<TimeSpan?>(), CancellationToken.None);
        actionCalled.Should().BeTrue();
        actual.Should().Be(expected);
        _database.Received(0).CreateTransaction();
        await _transaction.Received(0).HashSetAsync(redisKey, Arg.Any<HashEntry[]>(), CommandFlags.DemandMaster);
        await _transaction.Received(0).KeyExpireAsync(redisKey, Arg.Any<TimeSpan?>(), CommandFlags.DemandMaster | CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task Set_no_keys()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        IDictionary<string, string?> values = null!;
        Func<Task> act = async () => await Sut.SetAsync(region, values, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Set_with_special_key()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        var values = _fixture.Create<IDictionary<string, string?>>();
        values.Add("_extended_properties_", _fixture.Create<string>());
        Func<Task> act = async () => await Sut.SetAsync(region, values, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Set_redis_exception()
    {
        var keys = _fixture.CreateMany<string>().ToArray();
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var entries = _fixture.CreateMany<HashEntry>().ToArray();
        IDictionary<string, string?> values = entries.ToDictionary(k => k.Name.ToString(), k => (string?)k.Value);
        _transaction.ExecuteAsync().ThrowsAsync<Exception>();
        var actual = await Sut.SetAsync(region, values, _fixture.Create<TimeSpan?>(), CancellationToken.None);
        actual.Should().BeFalse();
        _database.Received(1).CreateTransaction();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Unknown_Region_v7()
    {
        var region = (Region)_fixture.Create<string>();
        var wasCalled = false;
        _redisCacheOptions.Version = 7;
        _database.KeyExpireTimeAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                wasCalled = true;
                return Task.FromResult(default(DateTime?));
            });
        var actual = await Sut.ExpireTimeAsync(region, CancellationToken.None);
        wasCalled.Should().BeTrue();
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Known_Region_v7()
    {
        var region = (Region)_fixture.Create<string>();
        var wasCalled = false;
        _redisCacheOptions.Version = 7;
        DateTimeOffset? expected = _fixture.Create<DateTimeOffset>();
        _database.KeyExpireTimeAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                wasCalled = true;
                return Task.FromResult((DateTime?)expected.GetValueOrDefault().UtcDateTime);
            });
        var actual = await Sut.ExpireTimeAsync(region, CancellationToken.None);
        wasCalled.Should().BeTrue();
        actual.Should().Be(expected.GetValueOrDefault());
    }

    [Fact]
    public async Task Read_ExpireTime_For_Known_Region_v6()
    {
        var region = (Region)_fixture.Create<string>();
        var wasCalled = false;
        _redisCacheOptions.Version = 6;
        TimeSpan? expected = _fixture.Create<TimeSpan>();
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                wasCalled = true;
                return expected;
            });
        var actual = await Sut.ExpireTimeAsync(region, CancellationToken.None);
        wasCalled.Should().BeTrue();
        actual.Should().NotBeNull();
    }
    [Fact]
    public async Task Read_ExpireTime_For_Known_Region_v6_not_default_expiration()
    {
        var region = (Region)_fixture.Create<string>();
        var wasCalled = false;
        _redisCacheOptions.Version = 6;
        _redisCacheOptions.DefaultExpiration = null;
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                wasCalled = true;
                return default(TimeSpan?);
            });
        var actual = await Sut.ExpireTimeAsync(region, CancellationToken.None);
        wasCalled.Should().BeTrue();
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_TimeToLive_For_Unknown_Region()
    {
        var region = (Region)_fixture.Create<string>();
        var wasCalled = false;
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                wasCalled = true;
                return Task.FromResult(default(TimeSpan?));
            });
        var actual = await Sut.TimeToLiveAsync(region, CancellationToken.None);
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

    [Fact]
    public async Task SetExtendedProperties_works_for_unknown_region()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        _database.KeyExistsAsync(redisKey, Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(false);

        var response = await Sut.SetExtendedPropertiesAsync<string>(region, expected, CancellationToken.None);
        response.Should().BeFalse();
        await _database.Received(1).KeyExistsAsync(redisKey, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().HashSetAsync(redisKey, Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>());
        await _database.DidNotReceive().HashDeleteAsync(redisKey, Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SetExtendedProperties_works_for_known_region()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        _database.KeyExistsAsync(redisKey, Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(true);

        await Sut.SetExtendedPropertiesAsync<string>(region, expected, CancellationToken.None);
        await _database.Received().KeyExistsAsync(redisKey, Arg.Any<CommandFlags>());
        await _database.Received().HashSetAsync(redisKey, Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
        await _database.DidNotReceive().HashDeleteAsync(redisKey, Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SetExtendedProperties_works_with_no_props()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        _database.KeyExistsAsync(redisKey, Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(true);

        await Sut.SetExtendedPropertiesAsync<string>(region, new Dictionary<string, string?>(), CancellationToken.None);
        await _database.Received().KeyExistsAsync(redisKey, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().HashSetAsync(redisKey, Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
        await _database.Received().HashDeleteAsync(redisKey, Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SetExtendedProperties_throw_exception()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        _database.KeyExistsAsync(redisKey, Arg.Any<CommandFlags>())
            .Throws(new Exception());

        var actual = await Sut.SetExtendedPropertiesAsync<string>(region, new Dictionary<string, string?>(), CancellationToken.None);
        await _database.Received().KeyExistsAsync(redisKey, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().HashSetAsync(redisKey, Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
        await _database.DidNotReceive().HashDeleteAsync(redisKey, Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task GetExtendedProperties_works_as_expected()
    {
        Region region = _fixture.Create<string>();
        RedisKey redisKey = string.Join(':', _redisCacheOptions.InstanceName, region).ToLowerInvariant();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        _database.KeyExistsAsync(redisKey, Arg.Any<CommandFlags>())
            .Throws(new Exception());
        _database.HashGetAsync(redisKey, Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(new RedisValue(JsonConvert.SerializeObject(expected)));
        var actual = await Sut.GetExtendedPropertiesAsync(region, CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _database = _fixture.Freeze<IDatabase>();
        _transaction = _fixture.Freeze<ITransaction>();
        _clock = _fixture.Freeze<ISystemClock>();
        _clock.UtcNow.Returns(c => _now);
        _policyHolder = _fixture.Freeze<IPolicyHolder>();
        _policyHolder.AsyncPolicy.Returns(Polly.Policy.NoOpAsync());
        _database.CreateTransaction().Returns(_transaction);
        _redisCacheOptions = new RedisCacheOptions
        {
            InstanceName = _fixture.Create<string>(),
            DefaultExpiration = TimeSpan.FromSeconds(Random.Shared.Next(1, 100)),
            Clock = _clock,
            EntryFactory = new TestCacheEntryFactory()
        };
        _fixture.Inject(Options.Create(_redisCacheOptions));
        _serializer = new JsonSerializer();
        _fixture.Inject(_serializer);
        return Task.CompletedTask;
    }
}
