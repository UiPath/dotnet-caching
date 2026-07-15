using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using UiPath.Caching;
using UiPath.Caching.Tests.Broadcast;

namespace UiPath.Caching.Tests;

public class MultilayerCacheTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ICache _innerCache = default!;
    private IChangeTokenFactory _changeTokenFactory = default!;
    private ITopicFactory _topicFactory = default!;
    private ITopicProviderWithConnectionState _topicProvider = default!;
    private ITopic<ICacheEvent> _topic = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private ITopicKeyStrategy _topicKeyStrategy = default!;
    private IMemoryCache _memoryCache = default!;
    private ISystemClock _clock = default!;
    private IEventFormatterProxy<ICacheEvent> _formatter = default!;
    private ICacheEventFactory _cacheEventFactory = default!;
    private IMemoryCacheFactory _memoryCacheFactory = default!;
    private InMemoryRedisCacheOptions _options = default!;
    private TopicKey _topicKey = default!;
    private CacheKey _cacheKey = default!;
    private CacheKey _innerCacheKey = default!;
    private CacheKey _multiKey = default!;
    private CacheKey _innerMultiKey = default!;
    private ILogger _logger = default!;

    private MultilayerCache? _sut = null;

    private MultilayerCache Sut => _sut ??= _fixture.Create<MultilayerCache>();

    [Fact]
    public async Task Get_data_from_inner_cache()
    {
        var expected = _fixture.Create<string>();
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = expected });

        var actual = await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        Sut.Name.Should().NotBeNullOrWhiteSpace();
        _changeTokenFactory.Received(1).Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>());
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        actual.Should().Be(expected);
        _logger.ReceivedCalls().Should().Contain(c => c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! == LogLevel.Trace);
    }

    [Fact]
    public async Task Get_does_not_call_inner_ExpireTimeAsync_separately()
    {
        var expected = _fixture.Create<string>();
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = expected, Expiration = _fixture.Create<DateTimeOffset>() });

        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        await _innerCache.DidNotReceive().ExpireTimeAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _innerCache.DidNotReceive().TimeToLiveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multi_get_does_not_call_inner_ExpireTimeAsync_separately()
    {
        var expected = _fixture.Create<string>();
        _innerCache.GetCacheEntriesAsync<string>(Arg.Is<CacheKey[]>(k => k.Contains(_innerCacheKey) && k.Contains(_innerMultiKey)), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new KeyValuePair<CacheKey, ICacheEntry<string?>>[]
            {
                new(_innerCacheKey, new TestCacheEntry<string?> { Value = expected, Expiration = _fixture.Create<DateTimeOffset>() }),
                new(_innerMultiKey, new TestCacheEntry<string?> { Value = expected, Expiration = _fixture.Create<DateTimeOffset>() })
            });

        await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        await _innerCache.DidNotReceive().ExpireTimeAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _innerCache.DidNotReceive().ExpireTimeAsync<string>(_innerMultiKey, Arg.Any<CancellationToken>());
        await _innerCache.DidNotReceive().TimeToLiveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _innerCache.DidNotReceive().TimeToLiveAsync<string>(_innerMultiKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_data_from_memory_cache()
    {
        var expected = _fixture.Create<string>();
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string> { Value = expected };
                return true;
            });
        var actual = await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        actual.Should().Be(expected);
        await _innerCache.DidNotReceive().GetAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        _logger.ReceivedCalls().Should().Contain(c => c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! == LogLevel.Trace);
    }

    [Fact]
    public async Task Get_null_key()
    {
        string? ns = null;
        Func<Task> act = async () => await Sut.GetAsync<object>(ns!, policy: null, token: testContextAccessor.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Multi_get_data_from_inner_cache()
    {
        var expected = _fixture.Create<string>();
        _innerCache.GetCacheEntriesAsync<string>(Arg.Is<CacheKey[]>(k => k.Contains(_innerCacheKey) && k.Contains(_innerMultiKey)), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new KeyValuePair<CacheKey, ICacheEntry<string?>>[]
            {
                new(_innerCacheKey, new TestCacheEntry<string?> { Value = expected }),
                new(_innerMultiKey, new TestCacheEntry<string?> { Value = expected })
            });

        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        _changeTokenFactory.Received(1).Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>());
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        _changeTokenFactory.Received(1).Create(_innerMultiKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>());
        _memoryCache.Received(1).CreateEntry(_innerMultiKey);
        actual.Should().BeEquivalentTo(new KeyValuePair<CacheKey, string?>[] { new (_cacheKey, expected), new (_multiKey, expected) });
    }

    [Fact]
    public async Task Multi_get_data_from_memory_cache()
    {
        var expected = _fixture.Create<string>();
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string> { Value = expected };
                return true;
            });
        _memoryCache.TryGetValue(_innerMultiKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string> { Value = expected };
                return true;
            });
        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeEquivalentTo(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, expected), new(_multiKey, expected) });
        await _innerCache.DidNotReceive().GetAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multi_get_null_key()
    {
        string? ns = null;
        Func<Task> act = async () => await Sut.GetAsync<object>(new CacheKey[] { ns! }, policy: null, token: testContextAccessor.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetCacheEntries_preserves_input_order_with_mixed_local_and_remote_hits()
    {
        var localValue = _fixture.Create<string>();
        var remoteValue = _fixture.Create<string>();
        // _cacheKey -> local hit, _multiKey -> remote hit
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string?> { Value = localValue };
                return true;
            });
        _innerCache.GetCacheEntriesAsync<string>(Arg.Is<CacheKey[]>(k => k != null && k.Length == 1 && k.Contains(_innerMultiKey)), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new KeyValuePair<CacheKey, ICacheEntry<string?>>[]
            {
                new(_innerMultiKey, new TestCacheEntry<string?> { Value = remoteValue })
            });

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries[0].Key.Should().Be(_innerCacheKey);
        entries[0].Value.Value.Should().Be(localValue);
        entries[1].Key.Should().Be(_innerMultiKey);
        entries[1].Value.Value.Should().Be(remoteValue);
    }

    [Fact]
    public async Task GetCacheEntries_returns_one_entry_per_input_key_when_disconnected_without_use_primary_only()
    {
        _options.UseLocalOnlyWhenDisconnected = false;
        _options.ConnectionMonitorEnabled = true;
        _topicProvider.IsConnected.Returns(false);
        // _cacheKey has a stale local entry; _multiKey has none.
        var staleValue = _fixture.Create<string>();
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string?> { Value = staleValue };
                return true;
            });
        _innerCache.GetCacheEntriesAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new KeyValuePair<CacheKey, ICacheEntry<string?>>[]
            {
                new(_innerMultiKey, new TestCacheEntry<string?> { Value = default })
            });

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries[0].Key.Should().Be(_innerCacheKey);
        entries[0].Value.Value.Should().BeNull();
        _memoryCache.Received(1).Remove(_innerCacheKey);
    }

    [Fact]
    public async Task GetCacheEntry_returns_remote_when_inner_has_value()
    {
        var expected = _fixture.Create<string>();
        var expectedExpiration = _fixture.Create<DateTimeOffset>();
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = expected, Expiration = expectedExpiration });

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Value.Should().Be(expected);
        entry.Expiration.Should().Be(expectedExpiration);
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        await _innerCache.DidNotReceive().ExpireTimeAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCacheEntry_returns_default_entry_when_inner_returns_null()
    {
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = default });

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Should().NotBeNull();
        entry.Value.Should().BeNull();
        _memoryCache.DidNotReceive().CreateEntry(_innerCacheKey);
    }

    [Fact]
    public async Task GetCacheEntry_returns_local_when_disconnected_and_UseLocalOnlyWhenDisconnected_is_true()
    {
        var expected = _fixture.Create<string>();
        _options.UseLocalOnlyWhenDisconnected = true;
        _options.ConnectionMonitorEnabled = true;
        _topicProvider.IsConnected.Returns(false);
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string?> { Value = expected };
                return true;
            });

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Value.Should().Be(expected);
        await _innerCache.DidNotReceive().GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCacheEntry_returns_default_entry_when_disconnected_and_UseLocalOnlyWhenDisconnected_is_false()
    {
        var staleValue = _fixture.Create<string>();
        _options.UseLocalOnlyWhenDisconnected = false;
        _options.ConnectionMonitorEnabled = true;
        _topicProvider.IsConnected.Returns(false);
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string?> { Value = staleValue };
                return true;
            });

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Should().NotBeNull();
        entry.Value.Should().BeNull();
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.DidNotReceive().GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCacheEntries_returns_local_when_disconnected_and_UseLocalOnlyWhenDisconnected_is_true()
    {
        var expected = _fixture.Create<string>();
        _options.UseLocalOnlyWhenDisconnected = true;
        _options.ConnectionMonitorEnabled = true;
        _topicProvider.IsConnected.Returns(false);
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string?> { Value = expected };
                return true;
            });
        _memoryCache.TryGetValue(_innerMultiKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string?> { Value = expected };
                return true;
            });

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries[0].Value.Value.Should().Be(expected);
        entries[1].Value.Value.Should().Be(expected);
        await _innerCache.DidNotReceive().GetCacheEntriesAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCacheEntries_skips_inner_call_when_all_keys_hit_locally()
    {
        var expected = _fixture.Create<string>();
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string?> { Value = expected };
                return true;
            });
        _memoryCache.TryGetValue(_innerMultiKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string?> { Value = expected };
                return true;
            });

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries[0].Value.Value.Should().Be(expected);
        entries[1].Value.Value.Should().Be(expected);
        await _innerCache.DidNotReceive().GetCacheEntriesAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrAdd_data_from_inner_cache()
    {
        var expected = _fixture.Create<string>();
        var generatorExpected = _fixture.Create<string>();
        var generatorWasCalled = false;
        Func<CancellationToken, Task<string?>> generator = token =>
        {
            generatorWasCalled = true;
            return Task.FromResult((string?)generatorExpected);
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = expected });

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);
        generatorWasCalled.Should().BeFalse();
        actual.Should().Be(expected);
    }

    [Fact]
    public async Task GetOrAdd_data_from_inner_cache_default_expiration()
    {
        var expected = _fixture.Create<string>();
        var generatorExpected = _fixture.Create<string>();
        var generatorWasCalled = false;
        Func<CancellationToken, Task<string?>> generator = token =>
        {
            generatorWasCalled = true;
            return Task.FromResult((string?)generatorExpected);
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = expected });

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, (CachePolicy?)null, testContextAccessor.Current.CancellationToken);
        generatorWasCalled.Should().BeFalse();
        actual.Should().Be(expected);
    }

    [Fact]
    public void Dispose_can_be_called()
    {
        _options.ConnectionMonitorEnabled = true;
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetOrAdd_data_from_generator(bool innerCacheSet)
    {
        var generatorExpected = _fixture.Create<string>();
        var generatorWasCalled = false;
        Func<CancellationToken, Task<string?>> generator = token =>
        {
            generatorWasCalled = true;
            return Task.FromResult((string?)generatorExpected);
        };

        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = default });
        _innerCache.SetAsync(_innerCacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(_ => innerCacheSet);

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);
        generatorWasCalled.Should().BeTrue();
        _memoryCache.Received(innerCacheSet ? 1 : 0).CreateEntry(_innerCacheKey);
        await _innerCache.Received(1).SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        actual.Should().Be(generatorExpected);
        _logger.ReceivedCalls().Should().Contain(c => c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! == LogLevel.Debug);
    }

    [Fact]
    public async Task GetOrAdd_data_from_generator_default()
    {
        var generatorExpected = _fixture.Create<string>();
        var generatorWasCalled = false;
        Func<CancellationToken, Task<string?>> generator = token =>
        {
            generatorWasCalled = true;
            return Task.FromResult(default(string?));
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = default });

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);
        generatorWasCalled.Should().BeTrue();
        _memoryCache.Received(0).CreateEntry(_innerCacheKey);
        await _innerCache.Received(0).SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        actual.Should().BeNull();
    }

    [Fact]
    public async Task GetOrAdd_returns_cached_null_without_invoking_generator_when_inner_reports_Found()
    {
        var generatorWasCalled = false;
        Func<CancellationToken, Task<string?>> generator = _ =>
        {
            generatorWasCalled = true;
            return Task.FromResult<string?>("fresh");
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = null, Found = true, Expiration = DateTimeOffset.UtcNow.AddMinutes(5) });

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);

        generatorWasCalled.Should().BeFalse("cached-null is a hit, not a miss");
        actual.Should().BeNull();
    }

    [Fact]
    public async Task GetOrAdd_stores_generator_null_when_CacheNullValues_true()
    {
        _options.CacheNullValues = true;
        _sut = null; // rebuild with updated options
        var generatorWasCalled = false;
        Func<CancellationToken, Task<string?>> generator = _ =>
        {
            generatorWasCalled = true;
            return Task.FromResult<string?>(null);
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = null }); // miss (Found defaults to Value is not null)

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);

        generatorWasCalled.Should().BeTrue();
        actual.Should().BeNull();
        await _innerCache.Received().SetAsync<string?>(_innerCacheKey, default, Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrAdd_does_not_store_generator_null_when_CacheNullValues_false()
    {
        _options.CacheNullValues = false;
        var generatorWasCalled = false;
        Func<CancellationToken, Task<string?>> generator = _ =>
        {
            generatorWasCalled = true;
            return Task.FromResult<string?>(null);
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = null });

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);

        generatorWasCalled.Should().BeTrue();
        actual.Should().BeNull();
        await _innerCache.DidNotReceive().SetAsync<string?>(_innerCacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_default_value()
    {
        _innerCache.GetAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(default(string?));

        var actual = await Sut.SetAsync(_cacheKey, default(string), _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_null_with_CacheNullValues_persists_through_inner_cache()
    {
        _options.CacheNullValues = true;
        _sut = null;
        _innerCache.SetAsync<string?>(_innerCacheKey, default, Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        await Sut.SetAsync(_cacheKey, default(string), _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);

        await _innerCache.DidNotReceive().RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _innerCache.Received(1).SetAsync<string?>(_innerCacheKey, default, Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multi_set_keeps_null_entries_in_set_path_when_CacheNullValues_true()
    {
        _options.CacheNullValues = true;
        _sut = null;
        _innerCache.SetAsync<string?>(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var pairs = new KeyValuePair<CacheKey, string?>[]
        {
            new(_cacheKey, null),
            new(_multiKey, _fixture.Create<string>()),
        };
        await Sut.SetAsync(pairs, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);

        await _innerCache.DidNotReceive().RemoveAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CancellationToken>());
        await _innerCache.Received(1).SetAsync<string?>(
            Arg.Is<KeyValuePair<CacheKey, string?>[]>(p => p != null && p.Length == 2 && p.Any(kv => kv.Value == null)),
            Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multi_set_forwards_caller_expiration_to_inner_cache()
    {
        _options.CacheNullValues = true;
        _sut = null;
        _innerCache.SetAsync<string?>(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var ttl = TimeSpan.FromMinutes(7);

        var pairs = new KeyValuePair<CacheKey, string?>[]
        {
            new(_cacheKey, null),
            new(_multiKey, _fixture.Create<string>()),
        };
        await Sut.SetAsync(pairs, ttl, policy: null, token: testContextAccessor.Current.CancellationToken);

        await _innerCache.Received(1).SetAsync<string?>(
            Arg.Any<KeyValuePair<CacheKey, string?>[]>(),
            Arg.Is<DateTimeOffset?>(exp => exp.HasValue && exp.Value > _clock.UtcNow), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_default_value_default_expiration()
    {
        _innerCache.GetAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(default(string?));

        var actual = await Sut.SetAsync(_cacheKey, default(string), policy: null, token: testContextAccessor.Current.CancellationToken);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_value_value()
    {
        var value = _fixture.Create<string>();
        _innerCache.GetAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(default(string?));
        _innerCache.SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        var actual = await Sut.SetAsync(_cacheKey, value, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);
        _memoryCache.Received(0).Remove(_innerCacheKey);
        await _innerCache.Received(0).RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        await _innerCache.Received(1).SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_value_inner_cache_throw_exception()
    {
        var value = _fixture.Create<string>();
        _innerCache.SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception());

        var actual = await Sut.SetAsync(_cacheKey, value, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();

        actual = await Sut.SetAsync(_cacheKey, value, _fixture.Create<DateTimeOffset>(), token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Multi_set_default_value()
    {
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);
        _innerCache.GetAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(default(string?));
        _innerCache.GetAsync<string>(_innerMultiKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(default(string?));

        var actual = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new ( _cacheKey, default(string) ), new ( _multiKey, default(string) ) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        _memoryCache.Received(1).Remove(_innerMultiKey);
        await _innerCache.Received(1).RemoveAsync<string>(Arg.Is<CacheKey[]>(c => c.Contains(_innerMultiKey) && c.Contains(_innerCacheKey)), Arg.Any<CancellationToken>());
        await _topic.Received(2).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multi_set_default_value_default_expiration()
    {
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);
        _innerCache.GetAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(default(string?));
        _innerCache.GetAsync<string>(_innerMultiKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(default(string?));

        var actual = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, default(string)), new(_multiKey, default(string)) }, policy: null, token: testContextAccessor.Current.CancellationToken);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        _memoryCache.Received(1).Remove(_innerMultiKey);
        await _innerCache.Received(1).RemoveAsync<string>(Arg.Is<CacheKey[]>(c => c.Contains(_innerMultiKey) && c.Contains(_innerCacheKey)), Arg.Any<CancellationToken>());
        await _topic.Received(2).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multi_set_value_value()
    {
        var value = _fixture.Create<string>();
        _innerCache.GetAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(default(string?));
        _innerCache.GetAsync<string>(_innerMultiKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(default(string?));
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        var actual = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);
        _memoryCache.Received(0).Remove(_innerCacheKey);
        _memoryCache.Received(0).Remove(_innerMultiKey);
        await _innerCache.Received(0).RemoveAsync<string>(Arg.Is<CacheKey[]>(c => c.Contains(_innerMultiKey) || c.Contains(_innerCacheKey)), Arg.Any<CancellationToken>());
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        _memoryCache.Received(1).CreateEntry(_innerMultiKey);
        await _topic.Received(2).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multi_set_value_inner_cache_throw_exception()
    {
        var value = _fixture.Create<string>();
        _innerCache.SetAsync(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception());

        var actual = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, default(string)), new(_multiKey, default(string)) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();

        actual = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, default(string)), new(_multiKey, default(string)) }, policy: null, token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_default_value()
    {
        _innerCache.GetAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(default(string?));
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(_ => new Exception());
        var actual = await Sut.RemoveAsync<string>(_cacheKey, token: testContextAccessor.Current.CancellationToken);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        actual.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task Remove_default_value_error(bool removed, bool eventPublished, bool expected)
    {
        _innerCache.GetAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(default(string?));
        _innerCache.RemoveAsync<string>(_innerCacheKey, testContextAccessor.Current.CancellationToken)
            .Returns(_ => removed);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => eventPublished);
        var actual = await Sut.RemoveAsync<string>(_cacheKey, token: testContextAccessor.Current.CancellationToken);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        actual.Should().Be(expected);
    }

    [Fact]
    public async Task Remove_evict_active_token()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock,
            CompactionPercentage = 0.3,
            ExpirationScanFrequency = TimeSpan.FromSeconds(2),
        }));

        var expected = _fixture.Create<string>();
        _innerCache.GetAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(expected);
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var fi = _fixture.Create<IOptions<InMemoryRedisCacheOptions>>();
        var actual = await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        await Sut.RemoveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await token.AssertIsDisposed();
    }

    [Fact]
    public async Task Remove_evict_active_token_callback()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock
        }));

        var expected = _fixture.Create<string>();

        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = expected, Expiration = _clock.UtcNow.AddDays(1) });
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var actual = await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        token.HasChanged = true;
        token.InvokeCallbacks();
        _memoryCache.TryGetValue(_innerCacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_evict_token_non_active()
    {
        _clock = _fixture.Freeze<ISystemClock>();
        var now = DateTimeOffset.UtcNow;
        _clock.UtcNow.Returns(now);
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock,
        }));

        var expected = _fixture.Create<string>();
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = expected, Expiration = now.AddDays(1) });
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = false,
            HasChanged = false
        };

        _changeTokenFactory.Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var actual = await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        _memoryCache.TryGetValue(_innerCacheKey, out _).Should().BeTrue();
        token.HasChanged = true;
        _memoryCache.TryGetValue(_innerCacheKey, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task Multi_remove_default_value_error(bool removed, bool eventPublished, bool expected)
    {
        _innerCache.GetAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(default(string?));
        _innerCache.GetAsync<string>(_innerMultiKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(default(string?));

        _innerCache.RemoveAsync<string>(Arg.Is<CacheKey[]>(c => c.Contains(_innerMultiKey) && c.Contains(_innerCacheKey)), testContextAccessor.Current.CancellationToken)
            .Returns(_ => removed);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => eventPublished);
        var actual = await Sut.RemoveAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, token: testContextAccessor.Current.CancellationToken);
        await _innerCache.Received(1).RemoveAsync<string>(Arg.Is<CacheKey[]>(c => c.Contains(_innerMultiKey) && c.Contains(_innerCacheKey)), Arg.Any<CancellationToken>());
        
        if (removed)
        {
            _memoryCache.Received(1).Remove(_innerCacheKey);
            _memoryCache.Received(1).Remove(_innerMultiKey);

            // If one event is not published, the second event publish is skipped
            await _topic.Received(eventPublished ? 2 : 1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        }
        actual.Should().Be(expected);
    }

    [Fact]
    public async Task Multi_remove_evict_active_token()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock,
            CompactionPercentage = 0.3,
            ExpirationScanFrequency = TimeSpan.FromSeconds(2),
        }));

        var expected = _fixture.Create<string>();
        _innerCache.GetCacheEntriesAsync<string>(Arg.Is<CacheKey[]>(k => k.Contains(_innerCacheKey)), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new KeyValuePair<CacheKey, ICacheEntry<string?>>[]
            {
                new(_innerCacheKey, new TestCacheEntry<string?> { Value = expected })
            });
        _innerCache.RemoveAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var fi = _fixture.Create<IOptions<InMemoryRedisCacheOptions>>();
        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        await Sut.RemoveAsync<string>(new CacheKey[] { _cacheKey }, testContextAccessor.Current.CancellationToken);
        await token.AssertIsDisposed();
    }

    [Fact]
    public async Task Multi_remove_evict_active_token_callback()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock
        }));

        var expected = _fixture.Create<string>();

        _innerCache.GetCacheEntriesAsync<string>(Arg.Is<CacheKey[]>(k => k.Contains(_innerCacheKey)), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new KeyValuePair<CacheKey, ICacheEntry<string?>>[]
            {
                new(_innerCacheKey, new TestCacheEntry<string?> { Value = expected, Expiration = _clock.UtcNow.AddDays(1) })
            });
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        token.HasChanged = true;
        token.InvokeCallbacks();
        _memoryCache.TryGetValue(_innerCacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Multi_remove_evict_token_non_active()
    {
        _clock = _fixture.Freeze<ISystemClock>();
        var now = DateTimeOffset.UtcNow;
        _clock.UtcNow.Returns(now);
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock,
        }));

        var expected = _fixture.Create<string>();
        _innerCache.GetCacheEntriesAsync<string>(Arg.Is<CacheKey[]>(k => k.Contains(_innerCacheKey)), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new KeyValuePair<CacheKey, ICacheEntry<string?>>[]
            {
                new(_innerCacheKey, new TestCacheEntry<string?> { Value = expected, Expiration = now.AddDays(1) })
            });
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = false,
            HasChanged = false
        };

        _changeTokenFactory.Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        _memoryCache.TryGetValue(_innerCacheKey, out _).Should().BeTrue();
        token.HasChanged = true;
        _memoryCache.TryGetValue(_innerCacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_value_default_expiration()
    {
        await Sut.RefreshAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RefreshAsync<string>(_innerCacheKey, Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_value_TimeSpan()
    {
        var expiration = _fixture.Create<TimeSpan?>();
        await Sut.RefreshAsync<string>(_cacheKey, expiration, token: testContextAccessor.Current.CancellationToken);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RefreshAsync<string>(_innerCacheKey, Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_value_DateTimeOffset()
    {
        var expiration = DateTimeOffset.UtcNow.AddDays(1);
        await Sut.RefreshAsync<string>(_cacheKey, expiration, token: testContextAccessor.Current.CancellationToken);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RefreshAsync<string>(_innerCacheKey, Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_inner_cache_exception_timespan(bool eventFired)
    {
        var expiration = _fixture.Create<TimeSpan?>();
        _innerCache.RefreshAsync<string>(_innerCacheKey, Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception());
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => eventFired);
        await Sut.RefreshAsync<string>(_cacheKey, expiration, token: testContextAccessor.Current.CancellationToken);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(eventFired ? 1 : 0).RefreshAsync<string>(_innerCacheKey, Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_inner_cache_exception_DateTimeOffset(bool eventFired)
    {
        var expiration = DateTimeOffset.UtcNow.AddDays(1);
        _innerCache.RefreshAsync<string>(_innerCacheKey, Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception());
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => eventFired);
        await Sut.RefreshAsync<string>(_cacheKey, expiration, token: testContextAccessor.Current.CancellationToken);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(eventFired ? 1 : 0).RefreshAsync<string>(_innerCacheKey, Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Contains_in_inner_cache()
    {
        var expected = _fixture.Create<bool>();
        var memoryCacheCalled = false;
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(callInfo =>
            {
                memoryCacheCalled = true;
                return false;
            });
        _innerCache.ContainsAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>())
            .Returns(expected);
        var actual = await Sut.ContainsAsync<string>(_cacheKey, token: testContextAccessor.Current.CancellationToken);
        actual.Should().Be(expected);
        memoryCacheCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_in_memory_cache()
    {
        var expected = _fixture.Create<bool>();
        var memoryCacheCalled = false;
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                memoryCacheCalled = true;
                return true;
            });
        var actual = await Sut.ContainsAsync<string>(_cacheKey, token: testContextAccessor.Current.CancellationToken);
        await _innerCache.DidNotReceive().ContainsAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        actual.Should().Be(expected);
        memoryCacheCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_in_inner_cache_exception()
    {
        var expected = _fixture.Create<bool>();
        var memoryCacheCalled = false;
        _memoryCache.TryGetValue(Arg.Is<object>(o => o != null && (o.ToString() ?? string.Empty).Contains(_cacheKey)), out Arg.Any<object?>())
            .Returns(x =>
            {
                memoryCacheCalled = true;
                return false;
            });

        _innerCache.ContainsAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception());
        var actual = await Sut.ContainsAsync<string>(_cacheKey, token: testContextAccessor.Current.CancellationToken);
        actual.Should().Be(false);
        memoryCacheCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Key()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock
        }));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(token);
        _innerCache.SetAsync<int?>(_innerCacheKey, Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var expiration = _clock.UtcNow.AddYears(1);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);
        await Sut.SetAsync<int?>(_cacheKey, 1, expiration, token: testContextAccessor.Current.CancellationToken);
        var actual = await Sut.ExpireTimeAsync<int?>(_cacheKey, testContextAccessor.Current.CancellationToken);
        expiration.Should().Be(actual);
    }

    [Fact]
    public async Task Read_ExpireTimeToLive_For_Key()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock
        }));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };

        _changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(_ => token);
        _innerCache.SetAsync<int?>(_innerCacheKey, Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);
        var expiration = TimeSpan.FromDays(1);
        await Sut.SetAsync<int?>(_cacheKey, 1, expiration, token: testContextAccessor.Current.CancellationToken);
        var actual = await Sut.TimeToLiveAsync<int?>(_cacheKey, testContextAccessor.Current.CancellationToken);
        expiration.Should().BeCloseTo(actual.GetValueOrDefault(), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task When_no_inner_cache_expire_time_use_max()
    {
        var expected = _fixture.Create<string>();
        Func<CancellationToken, Task<string?>> generator = token => Task.FromResult((string?)expected);
        var cacheEntry = _fixture.Freeze<Microsoft.Extensions.Caching.Memory.ICacheEntry>();
        _memoryCache.CreateEntry(Arg.Any<object>())
            .Returns(cacheEntry);

        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = expected, Expiration = DateTimeOffset.MaxValue });
        _options.DefaultExpiration = null;
        _ = await Sut.GetOrAddAsync(_cacheKey, generator, expiration: default(DateTimeOffset?), token: testContextAccessor.Current.CancellationToken);
        cacheEntry.AbsoluteExpiration.Should().Be(DateTimeOffset.MaxValue);
    }

    [Fact]
    public async Task Get_returns_local_when_disconnected_and_UseLocalOnlyWhenDisconnected_is_true()
    {
        var expected = _fixture.Create<string>();
        _options.UseLocalOnlyWhenDisconnected = true;
        _topicProvider.IsConnected.Returns(false);
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string> { Value = expected };
                return true;
            });

        var actual = await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        actual.Should().Be(expected);
        await _innerCache.DidNotReceive().GetAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_returns_default_when_disconnected_and_UseLocalOnlyWhenDisconnected_is_false()
    {
        var expected = _fixture.Create<string>();
        _options.UseLocalOnlyWhenDisconnected = false;
        _topicProvider.IsConnected.Returns(false);
        _options.ConnectionMonitorEnabled = true;
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string> { Value = expected };
                return true;
            });

        var actual = await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeNull();
        _memoryCache.Received(1).Remove(_innerCacheKey);
    }

    [Fact]
    public async Task Multi_get_returns_local_when_disconnected_and_UseLocalOnlyWhenDisconnected_is_true()
    {
        var expected = _fixture.Create<string>();
        _options.UseLocalOnlyWhenDisconnected = true;
        _topicProvider.IsConnected.Returns(false);
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string> { Value = expected };
                return true;
            });
        _memoryCache.TryGetValue(_innerMultiKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string> { Value = expected };
                return true;
            });

        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeEquivalentTo(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, expected), new(_multiKey, expected) });
        await _innerCache.DidNotReceive().GetAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multi_get_returns_default_when_disconnected_and_UseLocalOnlyWhenDisconnected_is_false()
    {
        var expected = _fixture.Create<string>();
        _options.UseLocalOnlyWhenDisconnected = false;
        _topicProvider.IsConnected.Returns(false);
        _options.ConnectionMonitorEnabled = true;
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string> { Value = expected };
                return true;
            });
        _memoryCache.TryGetValue(_innerMultiKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string> { Value = expected };
                return true;
            });

        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeEmpty();
        _memoryCache.Received(1).Remove(_innerCacheKey);
        _memoryCache.Received(1).Remove(_innerMultiKey);
    }

    [Fact]
    public async Task Set_uses_local_only_when_disconnected_and_UseLocalOnlyWhenDisconnected_is_true()
    {
        var value = _fixture.Create<string>();
        _options.UseLocalOnlyWhenDisconnected = true;
        _options.LocalMaxExpirationDisconnected = TimeSpan.FromMinutes(5);
        _options.ConnectionMonitorEnabled = true;
        _topicProvider.IsConnected.Returns(false);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        var actual = await Sut.SetAsync(_cacheKey, value, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeTrue();
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        await _innerCache.DidNotReceive().SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multi_set_uses_local_only_when_disconnected_and_UseLocalOnlyWhenDisconnected_is_true()
    {
        var value = _fixture.Create<string>();
        _options.UseLocalOnlyWhenDisconnected = true;
        _options.LocalMaxExpirationDisconnected = TimeSpan.FromMinutes(5);
        _options.ConnectionMonitorEnabled = true;
        _topicProvider.IsConnected.Returns(false);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        var actual = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeTrue();
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        _memoryCache.Received(1).CreateEntry(_innerMultiKey);
        await _innerCache.DidNotReceive().SetAsync(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrAdd_uses_local_only_when_disconnected_and_UseLocalOnlyWhenDisconnected_is_true()
    {
        var generatorExpected = _fixture.Create<string>();
        var generatorWasCalled = false;
        Func<CancellationToken, Task<string?>> generator = token =>
        {
            generatorWasCalled = true;
            return Task.FromResult((string?)generatorExpected);
        };

        _options.UseLocalOnlyWhenDisconnected = true;
        _options.LocalMaxExpirationDisconnected = TimeSpan.FromMinutes(5);
        _options.ConnectionMonitorEnabled = true;
        _topicProvider.IsConnected.Returns(false);

        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = default });

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);
        generatorWasCalled.Should().BeTrue();
        actual.Should().Be(generatorExpected);
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        await _innerCache.DidNotReceive().SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Constructor_throws_when_LocalMaxExpirationDisconnected_is_greater_than_LocalMaxExpiration()
    {
        _options.LocalMaxExpiration = TimeSpan.FromMinutes(5);
        _options.LocalMaxExpirationDisconnected = TimeSpan.FromMinutes(10);

        Action act = () => _fixture.Create<MultilayerCache>();

        act.Should().Throw<Exception>()
            .Which.GetBaseException()
            .Should().BeOfType<OptionsValidationException>()
            .Which.Message.Should().Contain("LocalExpirationDisconnected")
            .And.Contain("must be less than or equal to")
            .And.Contain("LocalExpiration");
    }

    [Fact]
    public void Constructor_does_not_throw_when_LocalMaxExpirationDisconnected_equals_LocalMaxExpiration()
    {
        _options.LocalMaxExpiration = TimeSpan.FromMinutes(5);
        _options.LocalMaxExpirationDisconnected = TimeSpan.FromMinutes(5);

        Action act = () => _fixture.Create<MultilayerCache>();

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_does_not_throw_when_LocalMaxExpirationDisconnected_is_less_than_LocalMaxExpiration()
    {
        _options.LocalMaxExpiration = TimeSpan.FromMinutes(10);
        _options.LocalMaxExpirationDisconnected = TimeSpan.FromMinutes(5);

        Action act = () => _fixture.Create<MultilayerCache>();

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_does_not_throw_when_LocalMaxExpiration_is_null()
    {
        _options.LocalMaxExpiration = null;
        _options.LocalMaxExpirationDisconnected = TimeSpan.FromMinutes(5);

        Action act = () => _fixture.Create<MultilayerCache>();

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_does_not_throw_when_LocalMaxExpirationDisconnected_is_null()
    {
        _options.LocalMaxExpiration = TimeSpan.FromMinutes(5);
        _options.LocalMaxExpirationDisconnected = null;

        Action act = () => _fixture.Create<MultilayerCache>();

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(true, false)]  // innerCacheDisconnected = true (UseLocalOnlyWhenDisconnected=true, IsConnected=false)
    [InlineData(false, true)]  // innerCacheDisconnected = false (UseLocalOnlyWhenDisconnected=false, IsConnected=true)
    [InlineData(false, false)] // innerCacheDisconnected = false (UseLocalOnlyWhenDisconnected=false, IsConnected=false)
    [InlineData(true, true)]   // innerCacheDisconnected = false (UseLocalOnlyWhenDisconnected=true, IsConnected=true)
    public async Task SetAsync_calls_inner_cache_based_on_innerCacheDisconnected(bool useLocalOnlyWhenDisconnected, bool isConnected)
    {
        var value = _fixture.Create<string>();
        var innerCacheDisconnected = useLocalOnlyWhenDisconnected && !isConnected;
        _options.UseLocalOnlyWhenDisconnected = useLocalOnlyWhenDisconnected;
        _options.LocalMaxExpirationDisconnected = TimeSpan.FromMinutes(5);
        _options.ConnectionMonitorEnabled = true;
        _topicProvider.IsConnected.Returns(isConnected);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);
        _innerCache.SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var actual = await Sut.SetAsync(_cacheKey, value, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeTrue();
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);

        if (innerCacheDisconnected)
        {
            await _innerCache.DidNotReceive().SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
            await _topic.DidNotReceive().PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await _innerCache.Received(1).SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
            await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(true, false)]  // innerCacheDisconnected = true
    [InlineData(false, true)]  // innerCacheDisconnected = false
    [InlineData(false, false)] // innerCacheDisconnected = false
    [InlineData(true, true)]   // innerCacheDisconnected = false
    public async Task Multi_SetAsync_calls_inner_cache_based_on_innerCacheDisconnected(bool useLocalOnlyWhenDisconnected, bool isConnected)
    {
        var value = _fixture.Create<string>();
        var innerCacheDisconnected = useLocalOnlyWhenDisconnected && !isConnected;
        _options.UseLocalOnlyWhenDisconnected = useLocalOnlyWhenDisconnected;
        _options.LocalMaxExpirationDisconnected = TimeSpan.FromMinutes(5);
        _options.ConnectionMonitorEnabled = true;
        _topicProvider.IsConnected.Returns(isConnected);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);
        _innerCache.SetAsync(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var actual = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeTrue();
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        _memoryCache.Received(1).CreateEntry(_innerMultiKey);

        if (innerCacheDisconnected)
        {
            await _innerCache.DidNotReceive().SetAsync(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
            await _topic.DidNotReceive().PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await _innerCache.Received(1).SetAsync(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
            await _topic.Received(2).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(true, false)]  // innerCacheDisconnected = true
    [InlineData(false, true)]  // innerCacheDisconnected = false
    [InlineData(false, false)] // innerCacheDisconnected = false
    [InlineData(true, true)]   // innerCacheDisconnected = false
    public async Task GetOrAddAsync_calls_inner_cache_based_on_innerCacheDisconnected(bool useLocalOnlyWhenDisconnected, bool isConnected)
    {
        var generatorExpected = _fixture.Create<string>();
        var generatorWasCalled = false;
        Func<CancellationToken, Task<string?>> generator = token =>
        {
            generatorWasCalled = true;
            return Task.FromResult((string?)generatorExpected);
        };

        var innerCacheDisconnected = useLocalOnlyWhenDisconnected && !isConnected;
        _options.UseLocalOnlyWhenDisconnected = useLocalOnlyWhenDisconnected;
        _options.LocalMaxExpirationDisconnected = TimeSpan.FromMinutes(5);
        _options.ConnectionMonitorEnabled = true;
        _topicProvider.IsConnected.Returns(isConnected);

        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = default });
        _innerCache.SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);
        generatorWasCalled.Should().BeTrue();
        actual.Should().Be(generatorExpected);
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);

        if (innerCacheDisconnected)
        {
            await _innerCache.DidNotReceive().SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await _innerCache.Received(1).SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _cacheKey = _fixture.Create<string>();
        _innerCacheKey = ToInnerCacheKey<string>(_cacheKey);
        _topicKey = _fixture.Create<string>();
        _multiKey = _fixture.Create<string>();
        _innerMultiKey = ToInnerCacheKey<string>(_multiKey);

        _changeTokenFactory = _fixture.Freeze<IChangeTokenFactory>();
        _memoryCache = _fixture.Freeze<IMemoryCache>();
        _innerCache = _fixture.Freeze<ICache>();
        _logger = _fixture.Freeze<ILogger>();
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        _clock = new SystemClock();
        _options = new()
        {
            DefaultExpiration = TimeSpan.FromMinutes(10),
            EntryFactory = new TestCacheEntryFactory(),
        };

        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        _topicKeyStrategy = _fixture.Create<ITopicKeyStrategy>();
        _cacheKeyStrategy.GetCacheKey<string>(_cacheKey).Returns(_cacheKey);
        _topicKeyStrategy.GetTopicKey<string>().Returns(_topicKey);
        _topicFactory = _fixture.Freeze<ITopicFactory>();
        _topicProvider = _fixture.Freeze<ITopicProviderWithConnectionState>();
        _topic = _fixture.Freeze<ITopic<ICacheEvent>>();
        _topicFactory.Get(Arg.Any<string>()).Returns(_topicProvider);
        _topicProvider.Create(_topicKey).Returns(_topic);
        _memoryCacheFactory = _fixture.Freeze<IMemoryCacheFactory>();
        _memoryCacheFactory.Get(Arg.Any<IMemoryCacheOptions>())
            .Returns(c => _memoryCache);
        _fixture.Inject<IMultilayerCacheOptions>(_options);
        _fixture.Inject<IMemoryCacheOptions>(_options);
        _formatter = new CacheClearEventFormatterProxy();
        _fixture.Inject(_formatter);
        _cacheEventFactory = _fixture.Freeze<ICacheEventFactory>();
        _cacheEventFactory.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
            .Returns(c =>
                new TestCacheEvent
                {
                    Id = c.ArgAt<string?>(3),
                    Data = c.Arg<CacheEventData>(),
                    Type = c.ArgAt<string>(1),
                }
            );
        return ValueTask.CompletedTask;
    }

    protected virtual CacheKey ToInnerCacheKey<T>(CacheKey key)
    {
        return key;
    }

    public interface ITopicProviderWithConnectionState : ITopicProvider, IConnectionState
    {
    }
}
