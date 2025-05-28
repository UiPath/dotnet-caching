using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using NSubstitute.ExceptionExtensions;
using UiPath.Platform.Caching;
using UiPath.Platform.Caching.Tests.Broadcast;

namespace UiPath.Platform.Caching.Tests;

public class MultilayerCacheTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ICache _innerCache = default!;
    private IChangeTokenFactory _changeTokenFactory = default!;
    private ITopicFactory _topicFactory = default!;
    private ITopicProvider _topicProvider = default!;
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

    private MultilayerCache? _sut = null;

    private MultilayerCache Sut => _sut ??= _fixture.Create<MultilayerCache>();

    [Fact]
    public async Task Get_data_from_inner_cache()
    {
        var expected = _fixture.Create<string>();
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expected);

        var actual = await Sut.GetAsync<string>(_cacheKey, token: CancellationToken.None);
        Sut.Name.Should().NotBeNullOrWhiteSpace();
        _changeTokenFactory.Received(1).Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>());
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        actual.Should().Be(expected);
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
        var actual = await Sut.GetAsync<string>(_cacheKey, token: CancellationToken.None);
        actual.Should().Be(expected);
        await _innerCache.DidNotReceive().GetAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_null_key()
    {
        string? ns = null;
        Func<Task> act = async () => await Sut.GetAsync<object>(ns!, token: CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Multi_get_data_from_inner_cache()
    {
        var expected = _fixture.Create<string>();
        _innerCache.GetAsync<string>(Arg.Is<CacheKey[]>(k => k.Contains(_innerCacheKey) && k.Contains(_innerMultiKey)), token: CancellationToken.None)
            .Returns(new KeyValuePair<CacheKey, string?>[] { new (_innerCacheKey, expected ), new ( _innerMultiKey, expected ) });

        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, token: CancellationToken.None);
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
        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, token: CancellationToken.None);
        actual.Should().BeEquivalentTo(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, expected), new(_multiKey, expected) });
        await _innerCache.DidNotReceive().GetAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multi_get_null_key()
    {
        string? ns = null;
        Func<Task> act = async () => await Sut.GetAsync<object>(new CacheKey[] { ns! }, token: CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
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
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expected);

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
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
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expected);

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, CancellationToken.None);
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

        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(default(string?));
        _innerCache.SetAsync(_innerCacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ => innerCacheSet);

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeTrue();
        _memoryCache.Received(innerCacheSet ? 1 : 0).CreateEntry(_innerCacheKey);
        await _innerCache.Received(1).SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        actual.Should().Be(generatorExpected);
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
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(default(string));

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeTrue();
        _memoryCache.Received(0).CreateEntry(_innerCacheKey);
        await _innerCache.Received(0).SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Set_default_value()
    {
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(default(string?));

        var actual = await Sut.SetAsync(_cacheKey, default(string), _fixture.Create<TimeSpan>(), CancellationToken.None);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_default_value_default_expiration()
    {
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(default(string?));

        var actual = await Sut.SetAsync(_cacheKey, default(string), CancellationToken.None);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_value_value()
    {
        var value = _fixture.Create<string>();
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(default(string?));
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), token: CancellationToken.None)
            .Returns(_ => true);

        var actual = await Sut.SetAsync(_cacheKey, value, _fixture.Create<TimeSpan>(), CancellationToken.None);
        _memoryCache.Received(0).Remove(_innerCacheKey);
        await _innerCache.Received(0).RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        await _innerCache.Received(1).SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_value_inner_cache_throw_exception()
    {
        var value = _fixture.Create<string>();
        _innerCache.SetAsync(_innerCacheKey, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception());

        var actual = await Sut.SetAsync(_cacheKey, value, _fixture.Create<TimeSpan>(), CancellationToken.None);
        actual.Should().BeFalse();

        actual = await Sut.SetAsync(_cacheKey, value, _fixture.Create<DateTimeOffset>(), CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Multi_set_default_value()
    {
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(default(string?));
        _innerCache.GetAsync<string>(_innerMultiKey, token: CancellationToken.None)
            .Returns(default(string?));

        var actual = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new ( _cacheKey, default(string) ), new ( _multiKey, default(string) ) }, _fixture.Create<TimeSpan>(), CancellationToken.None);
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
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(default(string?));
        _innerCache.GetAsync<string>(_innerMultiKey, token: CancellationToken.None)
            .Returns(default(string?));

        var actual = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, default(string)), new(_multiKey, default(string)) }, CancellationToken.None);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        _memoryCache.Received(1).Remove(_innerMultiKey);
        await _innerCache.Received(1).RemoveAsync<string>(Arg.Is<CacheKey[]>(c => c.Contains(_innerMultiKey) && c.Contains(_innerCacheKey)), Arg.Any<CancellationToken>());
        await _topic.Received(2).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multi_set_value_value()
    {
        var value = _fixture.Create<string>();
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(default(string?));
        _innerCache.GetAsync<string>(_innerMultiKey, token: CancellationToken.None)
            .Returns(default(string?));
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), token: CancellationToken.None)
            .Returns(_ => true);

        var actual = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<TimeSpan>(), CancellationToken.None);
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
        _innerCache.SetAsync(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception());

        var actual = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, default(string)), new(_multiKey, default(string)) }, _fixture.Create<TimeSpan>(), CancellationToken.None);
        actual.Should().BeFalse();

        actual = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, default(string)), new(_multiKey, default(string)) }, CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_default_value()
    {
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(default(string?));
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(_ => new Exception());
        var actual = await Sut.RemoveAsync<string>(_cacheKey, token: CancellationToken.None);
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
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(default(string?));
        _innerCache.RemoveAsync<string>(_innerCacheKey, CancellationToken.None)
            .Returns(_ => removed);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => eventPublished);
        var actual = await Sut.RemoveAsync<string>(_cacheKey, token: CancellationToken.None);
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
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expected);
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var fi = _fixture.Create<IOptions<InMemoryRedisCacheOptions>>();
        var actual = await Sut.GetAsync<string>(_cacheKey, token: CancellationToken.None);
        await Sut.RemoveAsync<string>(_cacheKey);
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

        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expected);
        _innerCache.ExpireTimeAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns((DateTimeOffset?)_clock.UtcNow.AddDays(1));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var actual = await Sut.GetAsync<string>(_cacheKey, token: CancellationToken.None);
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
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expected);
        _innerCache.ExpireTimeAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns((DateTimeOffset?)now.AddDays(1));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = false,
            HasChanged = false
        };

        _changeTokenFactory.Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var actual = await Sut.GetAsync<string>(_cacheKey, token: CancellationToken.None);
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
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(default(string?));
        _innerCache.GetAsync<string>(_innerMultiKey, token: CancellationToken.None)
            .Returns(default(string?));

        _innerCache.RemoveAsync<string>(Arg.Is<CacheKey[]>(c => c.Contains(_innerMultiKey) && c.Contains(_innerCacheKey)), CancellationToken.None)
            .Returns(_ => removed);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => eventPublished);
        var actual = await Sut.RemoveAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, token: CancellationToken.None);
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
        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expected);
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var fi = _fixture.Create<IOptions<InMemoryRedisCacheOptions>>();
        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey }, token: CancellationToken.None);
        await Sut.RemoveAsync<string>(new CacheKey[] { _cacheKey });
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

        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expected);
        _innerCache.ExpireTimeAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns((DateTimeOffset?)_clock.UtcNow.AddDays(1));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey }, token: CancellationToken.None);
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
        _innerCache.GetAsync<string>(Arg.Is<CacheKey[]>(k => k.Contains(_innerCacheKey)), token: CancellationToken.None)
            .Returns(new KeyValuePair<CacheKey, string?>[1] {new KeyValuePair<CacheKey, string?>(_innerCacheKey, expected)});
        _innerCache.ExpireTimeAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns((DateTimeOffset?)now.AddDays(1));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = false,
            HasChanged = false
        };

        _changeTokenFactory.Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey }, token: CancellationToken.None);
        _memoryCache.TryGetValue(_innerCacheKey, out _).Should().BeTrue();
        token.HasChanged = true;
        _memoryCache.TryGetValue(_innerCacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_value_default_expiration()
    {
        await Sut.RefreshAsync<string>(_cacheKey, token: CancellationToken.None);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RefreshAsync<string>(_innerCacheKey, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_value_TimeSpan()
    {
        var expiration = _fixture.Create<TimeSpan?>();
        await Sut.RefreshAsync<string>(_cacheKey, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RefreshAsync<string>(_innerCacheKey, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_value_DateTimeOffset()
    {
        var expiration = DateTimeOffset.UtcNow.AddDays(1);
        await Sut.RefreshAsync<string>(_cacheKey, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RefreshAsync<string>(_innerCacheKey, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_inner_cache_exception_timespan(bool eventFired)
    {
        var expiration = _fixture.Create<TimeSpan?>();
        _innerCache.RefreshAsync<string>(_innerCacheKey, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception());
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => eventFired);
        await Sut.RefreshAsync<string>(_cacheKey, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(eventFired ? 1 : 0).RefreshAsync<string>(_innerCacheKey, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_inner_cache_exception_DateTimeOffset(bool eventFired)
    {
        var expiration = DateTimeOffset.UtcNow.AddDays(1);
        _innerCache.RefreshAsync<string>(_innerCacheKey, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception());
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => eventFired);
        await Sut.RefreshAsync<string>(_cacheKey, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(eventFired ? 1 : 0).RefreshAsync<string>(_innerCacheKey, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
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
        _innerCache.ContainsAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expected);
        var actual = await Sut.ContainsAsync<string>(_cacheKey, token: CancellationToken.None);
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
        var actual = await Sut.ContainsAsync<string>(_cacheKey, token: CancellationToken.None);
        await _innerCache.DidNotReceive().ContainsAsync<string>(_innerCacheKey, token: CancellationToken.None);
        actual.Should().Be(expected);
        memoryCacheCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_in_inner_cache_exception()
    {
        var expected = _fixture.Create<bool>();
        var memoryCacheCalled = false;
        _memoryCache.TryGetValue(Arg.Is<object>(o => (o.ToString() ?? string.Empty).Contains(_cacheKey)), out Arg.Any<object?>())
            .Returns(x =>
            {
                memoryCacheCalled = true;
                return false;
            });

        _innerCache.ContainsAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .ThrowsAsync(new Exception());
        var actual = await Sut.ContainsAsync<string>(_cacheKey, token: CancellationToken.None);
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

        var expiration = _clock.UtcNow.AddYears(1);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);
        await Sut.SetAsync<int?>(_cacheKey, 1, expiration, CancellationToken.None);
        var actual = await Sut.ExpireTimeAsync<int?>(_cacheKey);
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
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);
        var expiration = TimeSpan.FromDays(1);
        await Sut.SetAsync<int?>(_cacheKey, 1, expiration, CancellationToken.None);
        var actual = await Sut.TimeToLiveAsync<int?>(_cacheKey);
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

        _innerCache.GetAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expected);
        _innerCache.ExpireTimeAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(default(DateTimeOffset?));
        _options.DefaultExpiration = null;
        _ = await Sut.GetOrAddAsync(_cacheKey, generator, default(DateTimeOffset?), CancellationToken.None);
        cacheEntry.AbsoluteExpiration.Should().Be(DateTimeOffset.MaxValue);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _cacheKey = _fixture.Create<string>();
        _innerCacheKey = ToInnerCacheKey<string>(_cacheKey);
        _topicKey = _fixture.Create<string>();
        _multiKey = _fixture.Create<string>();
        _innerMultiKey = ToInnerCacheKey<string>(_multiKey);

        _changeTokenFactory = _fixture.Freeze<IChangeTokenFactory>();
        _memoryCache = _fixture.Freeze<IMemoryCache>();
        _innerCache = _fixture.Freeze<ICache>();
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
        return Task.CompletedTask;
    }

    protected virtual CacheKey ToInnerCacheKey<T>(CacheKey key)
    {
        return key;
    }

    public interface ITopicProviderWithConnectionState : ITopicProvider, IConnectionState
    {
    }
}
