using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;
using UiPath.Platform.Caching;
using UiPath.Platform.Caching.Tests.Broadcast;

namespace UiPath.Platform.Caching.Tests;

public class MultilayerHashCacheTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private IHashCache _innerCache = default!;
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

    private MultilayerHashCache? _sut = null;
    private MultilayerHashCache Sut => _sut ??= _fixture.Create<MultilayerHashCache>();


    [Fact]
    public async Task Get_data_from_inner_cache()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);

        var actual = await Sut.GetAsync<string>(_cacheKey, token: CancellationToken.None);
        _changeTokenFactory.Received(1).Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>());
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Get_unknown_cacheKey()
    {
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = null
        };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);

        var actual = await Sut.GetAsync<string>(_cacheKey, _fixture.CreateMany<string>().ToArray(), token: CancellationToken.None);
        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_cache_entry()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);

        var actual = await Sut.GetCacheEntryAsync<string>(_cacheKey, token: CancellationToken.None);
        actual.Should().NotBeNull();
        actual.Value.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Get_known_item()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var field = expected.Keys.First();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .ReturnsForAnyArgs(_ => expectedCacheEntry);

        var actual = await Sut.GetItemAsync<string>(_cacheKey, field, token: CancellationToken.None);
        _changeTokenFactory.Received(1).Create(_innerCacheKey, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>());
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        actual.Should().Be(expected[field]);
    }

    [Fact]
    public async Task Get_item_unknown_key()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);

        var actual = await Sut.GetItemAsync<string>(_cacheKey, _fixture.Create<string>(), token: CancellationToken.None);
        _changeTokenFactory.Received(1).Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>());
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Get_item_unknown_key_unknown_cacheKey()
    {
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = null
        };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);

        var actual = await Sut.GetItemAsync<string>(_cacheKey, _fixture.Create<string>(), token: CancellationToken.None);
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Get_data_from_memory_cache()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };

        _memoryCache.TryGetValue(Arg.Any<object>(), out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = expectedCacheEntry;
                return true;
            });
        var actual = await Sut.GetAsync<string>(_cacheKey, token: CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
        await _innerCache.DidNotReceive().GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrAdd_data_from_inner_cache_timespan()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        var generatorExpected = _fixture.Create<IDictionary<string, string?>>();
        var generatorWasCalled = false;
        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = token =>
        {
            generatorWasCalled = true;
            return Task.FromResult(generatorExpected);
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeFalse();
        actual.Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData(HashCacheSetOption.HashReplace)]
    [InlineData(HashCacheSetOption.KeyReplace)]
    public async Task GetOrAdd_data_from_inner_cache_HashCacheSetOption(HashCacheSetOption hashCacheSetOption)
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        var generatorExpected = _fixture.Create<IDictionary<string, string?>>();
        var generatorWasCalled = false;
        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = token =>
        {
            generatorWasCalled = true;
            return Task.FromResult(generatorExpected);
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<DateTimeOffset>(), hashCacheSetOption, CancellationToken.None);
        generatorWasCalled.Should().BeFalse();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_data_from_inner_cache_datetime()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        var generatorExpected = _fixture.Create<IDictionary<string, string?>>();
        var generatorWasCalled = false;
        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = token =>
        {
            generatorWasCalled = true;
            return Task.FromResult(generatorExpected);
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<DateTimeOffset>(), CancellationToken.None);
        generatorWasCalled.Should().BeFalse();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_data_from_inner_cache_no_expiration()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        var generatorExpected = _fixture.Create<IDictionary<string, string?>>();
        var generatorWasCalled = false;
        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = token =>
        {
            generatorWasCalled = true;
            return Task.FromResult(generatorExpected);
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, CancellationToken.None);
        generatorWasCalled.Should().BeFalse();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Dispose_can_be_called()
    {
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetOrAdd_data_from_generator(bool innerCacheSet)
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>();
        var generatorExpected = _fixture.Create<IDictionary<string, string?>>();
        var generatorWasCalled = false;
        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = token =>
        {
            generatorWasCalled = true;
            return Task.FromResult(generatorExpected);
        };

        _innerCache.SetAsync(_innerCacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => innerCacheSet);

        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);
        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeTrue();
        _memoryCache.Received(innerCacheSet ? 1 : 0).CreateEntry(_innerCacheKey);
        await _innerCache.Received(1).SetAsync(_innerCacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>());
        actual.Should().BeEquivalentTo(generatorExpected);
    }

    [Fact]
    public async Task GetOrAdd_data_from_generator_default()
    {
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>();
        IDictionary<string, string?> generatorExpected = new Dictionary<string, string?>();
        var generatorWasCalled = false;
        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = token =>
        {
            generatorWasCalled = true;
            return Task.FromResult(generatorExpected);
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeTrue();
        _memoryCache.DidNotReceive().CreateEntry(_innerCacheKey);
        await _innerCache.DidNotReceive().SetAsync(_innerCacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>());
        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_default_value()
    {
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>();
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);

        await Sut.SetAsync(_cacheKey, new Dictionary<string, string?>(), _fixture.Create<TimeSpan>(), CancellationToken.None);
        _memoryCache.Received(1).Remove(Arg.Any<object>());
        await _innerCache.Received(1).RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_cacheKey_no_expiration()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>> { Value = expected };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None).Returns(expectedCacheEntry);
        await Sut.SetAsync(_cacheKey, expected, CancellationToken.None);
        _memoryCache.DidNotReceive().Remove(_innerCacheKey);
        await _innerCache.DidNotReceive().RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        await _innerCache.Received(1).SetAsync(_innerCacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_value()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>> { Value = expected };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None).Returns(expectedCacheEntry);
        _innerCache.SetAsync(_innerCacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(true);
        var actual = await Sut.SetAsync(_cacheKey, expected, _fixture.Create<TimeSpan>(), CancellationToken.None);
        actual.Should().BeTrue();
        _memoryCache.DidNotReceive().Remove(_innerCacheKey);
        await _innerCache.DidNotReceive().RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        await _innerCache.Received(1).SetAsync(_innerCacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_value_with_options()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>> { Value = expected };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None).Returns(expectedCacheEntry);
        _innerCache.SetAsync(_innerCacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(true);
        var options = new HashCacheEntryOptions(default, _fixture.Create<TimeSpan>(), _fixture.Create<IDictionary<string, string?>>());
        var actual = await Sut.SetAsync(_cacheKey, expected, options, CancellationToken.None);
        actual.Should().BeTrue();
        _memoryCache.DidNotReceive().Remove(_innerCacheKey);
        await _innerCache.DidNotReceive().RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        _memoryCache.Received(1).CreateEntry(_innerCacheKey);
        await _innerCache.Received(1).SetAsync(_innerCacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_empty_value_with_options()
    {
        IDictionary<string, string?> expected = new Dictionary<string, string?>();
        var options = new HashCacheEntryOptions(default, _fixture.Create<TimeSpan>(), _fixture.Create<IDictionary<string, string?>>());
        _innerCache.RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>()).Returns(true);
        var actual = await Sut.SetAsync(_cacheKey, expected, options, CancellationToken.None);
        actual.Should().BeTrue();
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        _memoryCache.DidNotReceive().CreateEntry(_innerCacheKey);
        await _innerCache.DidNotReceive().SetAsync(_innerCacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_value_inner_cache_throw_exception()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
#pragma warning disable CA2012 // Use ValueTasks correctly
        _innerCache.SetAsync(_innerCacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .ThrowsForAnyArgs(new Exception());
#pragma warning restore CA2012 // Use ValueTasks correctly

        var actual = await Sut.SetAsync(_cacheKey, expected, _fixture.Create<TimeSpan>(), CancellationToken.None);
        actual.Should().BeFalse();

        actual = await Sut.SetAsync(_cacheKey, expected, _fixture.Create<DateTimeOffset>(), CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_default_value_error()
    {
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(_ => new Exception());
        var actual = await Sut.RemoveAsync<string>(_cacheKey, CancellationToken.None);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_default_value()
    {
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(_ => new Exception());
        var actual = await Sut.RemoveAsync<string>(_cacheKey, CancellationToken.None);
        _memoryCache.Received(1).Remove(Arg.Any<object>());
        await _innerCache.Received(1).RemoveAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_evict_active_token()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock
        }));

        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };

        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
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

        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };

        _innerCache.GetCacheEntryAsync<string>(_cacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);
        _innerCache.ExpireTimeAsync<string>(_cacheKey, CancellationToken.None)
               .Returns(_clock.UtcNow.AddDays(1));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var actual = await Sut.GetAsync<string>(_cacheKey, token: CancellationToken.None);
        token.HasChanged = true;
        token.InvokeCallbacks();
        _memoryCache.TryGetValue(_cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_metadata_callback()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock
        }));

        var expected = _fixture.Create<IDictionary<string, string?>>();
        var metadata = _fixture.Create<IDictionary<string, string?>>();
        DateTimeOffset? expireDate = _clock.UtcNow.AddDays(1);
        ICacheEntry<IDictionary<string, string?>> cacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected,
            Metadata = _fixture.Create<IDictionary<string, string?>>(),
            Expiration = expireDate.GetValueOrDefault(),
        };

        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(cacheEntry);
        _innerCache.GetMetadataAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>())
            .Returns(default(IDictionary<string, string?>?));
        _innerCache.ExpireTimeAsync<string>(_innerCacheKey, CancellationToken.None)
               .Returns(_clock.UtcNow.AddDays(1));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false,
            MetadataHasChanged = true,
            Expiration = expireDate.GetValueOrDefault(),
        };
        var token2 = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false,
            MetadataHasChanged = false,
            Expiration = expireDate.GetValueOrDefault(),
        };

        _changeTokenFactory.ClearSubstitute();
        _changeTokenFactory.Create(_innerCacheKey.Name, Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token, c => token2);
        var actual = await Sut.GetAsync<string>(_cacheKey, token: CancellationToken.None);
        _memoryCache.TryGetValue(_innerCacheKey, out var bla).Should().BeTrue();
        token.InvokeCallbacks();
        _memoryCache.TryGetValue(_innerCacheKey, out var _).Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_metadata_callback_no_cache_props()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock
        }));

        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected,
            Metadata = _fixture.Create<IDictionary<string, string?>>(),
            Expiration = _clock.UtcNow.AddDays(1),
        };

        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
                .Returns(expectedCacheEntry);
        _innerCache.GetMetadataAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>())
            .Returns(default(IDictionary<string, string?>?));
        _innerCache.ExpireTimeAsync<string>(_innerCacheKey, CancellationToken.None)
               .Returns(_clock.UtcNow.AddDays(1));

        TestChangeToken? token = default;
        _changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c =>
            {
                token = new TestChangeToken
                {
                    ActiveChangeCallbacks = true,
                    HasChanged = false,
                    MetadataHasChanged = false,
                };
                return token;
            });
        var actual = await Sut.GetAsync<string>(_cacheKey, token: CancellationToken.None);
        token.Should().NotBeNull();
        _memoryCache.TryGetValue(_innerCacheKey, out _).Should().BeTrue();
        token!.HasChanged = true;
        token.InvokeCallbacks();
        _memoryCache.TryGetValue(_innerCacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_metadata_callback_cache_throw_exception()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock
        }));

        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected,
            Metadata = _fixture.Create<IDictionary<string, string?>>(),
            Expiration = _clock.UtcNow.AddDays(1)
        };

        TestChangeToken? token = default;
        _changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c =>
            {
                token = new TestChangeToken
                {
                    ActiveChangeCallbacks = true,
                    HasChanged = false,
                    MetadataHasChanged = false
                };
                return token;
            });
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);
        _innerCache.GetMetadataAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception());
        _innerCache.ExpireTimeAsync<string>(_innerCacheKey, CancellationToken.None)
               .Returns(_clock.UtcNow.AddDays(1));
        var actual = await Sut.GetAsync<string>(_cacheKey, token: CancellationToken.None);
        token.Should().NotBeNull();
        _memoryCache.TryGetValue(_innerCacheKey, out _).Should().BeTrue();
        _topicProvider.ClearSubstitute();

        _topicProvider.Create(_topicKey)
            .Returns(_ => throw new Exception());
        token!.HasChanged = true;
        token.MetadataHasChanged = true;
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

        var expected = _fixture.Create<IDictionary<string, string?>>();
        ICacheEntry<IDictionary<string, string?>> expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected,
            Expiration = _clock.UtcNow.AddDays(1)
        };
        _innerCache.GetCacheEntryAsync<string>(_innerCacheKey, token: CancellationToken.None)
            .Returns(expectedCacheEntry);
        _innerCache.ExpireTimeAsync<string>(_innerCacheKey, CancellationToken.None)
            .Returns(now.AddDays(1));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = false,
            HasChanged = false
        };
        _changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var actual = await Sut.GetAsync<string>(_cacheKey, token: CancellationToken.None);
        _memoryCache.TryGetValue(_innerCacheKey, out _).Should().BeTrue();
        token.HasChanged = true;
        _memoryCache.TryGetValue(_innerCacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_value_TimeSpan()
    {
        var expiration = _fixture.Create<TimeSpan?>();
        await Sut.RefreshAsync<string>(_cacheKey, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RefreshAsync<string>(_innerCacheKey, Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_value_no_expiration()
    {
        await Sut.RefreshAsync<string>(_cacheKey, CancellationToken.None);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RefreshAsync<string>(_innerCacheKey, Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_value_DateTimeOffset()
    {
        var expiration = DateTimeOffset.UtcNow.AddDays(1);
        await Sut.RefreshAsync<string>(_cacheKey, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(1).RefreshAsync<string>(_innerCacheKey, Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_inner_cache_exception_timespan(bool eventFired)
    {
        var expiration = _fixture.Create<TimeSpan?>();
        _innerCache.RefreshAsync<string>(_innerCacheKey, Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception());
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => eventFired);
        await Sut.RefreshAsync<string>(_cacheKey, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(eventFired ? 1 : 0).RefreshAsync<string>(_innerCacheKey, Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_inner_cache_exception_DateTimeOffset(bool eventFired)
    {
        var expiration = DateTimeOffset.UtcNow.AddDays(1);
        _innerCache.RefreshAsync<string>(_innerCacheKey, Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception());
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => eventFired);
        await Sut.RefreshAsync<string>(_cacheKey, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(_innerCacheKey);
        await _innerCache.Received(eventFired ? 1 : 0).RefreshAsync<string>(_innerCacheKey, Arg.Any<HashCacheEntryOptions>(), Arg.Any<CancellationToken>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Contains_in_inner_cache()
    {
        var expected = _fixture.Create<bool>();
        var memoryCacheCalled = false;
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                memoryCacheCalled = true;
                return false;
            });
        _innerCache.ContainsAsync<string>(_innerCacheKey, CancellationToken.None)
            .Returns(expected);
        var actual = await Sut.ContainsAsync<string>(_cacheKey, CancellationToken.None);
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
        var actual = await Sut.ContainsAsync<string>(_cacheKey, CancellationToken.None);
        await _innerCache.DidNotReceive().ContainsAsync<string>(_innerCacheKey, CancellationToken.None);
        actual.Should().Be(expected);
        memoryCacheCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_in_inner_cache_exception()
    {
        var expected = _fixture.Create<bool>();
        var memoryCacheCalled = false;
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                memoryCacheCalled = true;
                return false;
            });
        _innerCache.ContainsAsync<string>(_innerCacheKey, CancellationToken.None)
            .ThrowsAsync(new Exception());
        var actual = await Sut.ContainsAsync<string>(_cacheKey, CancellationToken.None);
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
        IChangeToken? token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(token);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);
        var expiration = _clock.UtcNow.AddYears(1);
        var values = _fixture.Create<IDictionary<string, int?>>();
        await Sut.SetAsync(_cacheKey, values, expiration, CancellationToken.None);
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
        var values = _fixture.Create<IDictionary<string, int?>>();
        await Sut.SetAsync(_cacheKey, values, expiration, CancellationToken.None);
        var actual = await Sut.TimeToLiveAsync<int?>(_cacheKey);
        expiration.Should().BeCloseTo(actual.GetValueOrDefault(), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task GetMetadata_from_memory()
    {
        var clock = new SystemClock();
        var logger = _fixture.Freeze<ILogger<MultilayerHashCache>>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = _fixture.Create<IDictionary<string, string?>>(),
            Metadata = expected
        };

        _memoryCache.TryGetValue(Arg.Any<object>(), out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = expectedCacheEntry;
                return true;
            });

        var values = _fixture.Create<IDictionary<string, int>>();
        var actual = await Sut.GetMetadataAsync<string>(_cacheKey, CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetMetadata_from_innerCache()
    {
        var clock = new SystemClock();
        var logger = _fixture.Freeze<ILogger<MultilayerHashCache>>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = _fixture.Create<IDictionary<string, string?>>(),
            Metadata = expected
        };

        _innerCache.GetMetadataAsync<string>(_innerCacheKey, Arg.Any<CancellationToken>())
            .Returns(expected);
        _memoryCache.TryGetValue(Arg.Any<object>(), out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = _fixture.Create<IDictionary<string, string?>>();
                return false;
            });

        var values = _fixture.Create<IDictionary<string, int>>();
        var actual = await Sut.GetMetadataAsync<string>(_cacheKey, CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task SetMetadata_works_as_exptected()
    {
        var clock = new SystemClock();
        var logger = _fixture.Freeze<ILogger<MultilayerHashCache>>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = _fixture.Create<IDictionary<string, string?>>(),
            Metadata = expected
        };

        _innerCache.GetMetadataAsync<string>(_cacheKey, Arg.Any<CancellationToken>())
            .Returns(expected);
        _memoryCache.TryGetValue(Arg.Any<object>(), out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = _fixture.Create<IDictionary<string, string?>>();
                return false;
            });

        var values = _fixture.Create<IDictionary<string, int>>();
        await Sut.SetMetadataAsync<string>(_cacheKey, expected, CancellationToken.None);
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetMetadata_works_exception()
    {
        var clock = new SystemClock();
        var logger = _fixture.Freeze<ILogger<MultilayerHashCache>>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = _fixture.Create<IDictionary<string, string?>>(),
            Metadata = expected
        };

        _innerCache.SetMetadataAsync<string>(_innerCacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception());
        _memoryCache.TryGetValue(_innerCacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = _fixture.Create<IDictionary<string, string?>>();
                return false;
            });

        var values = _fixture.Create<IDictionary<string, int>>();
        var response = await Sut.SetMetadataAsync<string>(_cacheKey, expected, CancellationToken.None);
        response.Should().BeFalse();
        await _topic.DidNotReceive().PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetMetadata_secondary_cash_not_set()
    {
        _innerCache.SetMetadataAsync<string>(_cacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var ex = await Sut.SetMetadataAsync<string>(_cacheKey, _fixture.Create<IDictionary<string, string?>>(), CancellationToken.None);
        ex.Should().BeFalse();
        _memoryCache.Received(0).TryGetValue(Arg.Any<object>(), out Arg.Any<object?>());
    }

    [Fact]
    public async Task SetMetadata_reads_expiration_from_memory()
    {
        _innerCache.SetMetadataAsync<string>(_cacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected,
            Expiration = _clock.UtcNow.AddSeconds(10)
        };

        _memoryCache.TryGetValue(Arg.Any<object>(), out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = expectedCacheEntry;
                return true;
            });
        var actual = await Sut.SetMetadataAsync<string>(_cacheKey, _fixture.Create<IDictionary<string, string?>>(), CancellationToken.None);
        actual.Should().BeTrue();
        _memoryCache.Received(1).TryGetValue(Arg.Any<object>(), out Arg.Any<object?>());
        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_no_inner_cache_expire_time_use_max()
    {
        var expected = _fixture.Create<IDictionary<string, string?>>();
        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = token => Task.FromResult(expected);

        _innerCache.ExpireTimeAsync<string>(_innerCacheKey, CancellationToken.None)
            .Returns(default(DateTimeOffset?));

        var cacheEntry = _fixture.Freeze<Microsoft.Extensions.Caching.Memory.ICacheEntry>();
        _memoryCache.CreateEntry(Arg.Any<object>())
            .Returns(cacheEntry);

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
        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        _topicKeyStrategy = _fixture.Create<ITopicKeyStrategy>();
        _cacheKeyStrategy.GetCacheKey<string>(_cacheKey).Returns(_innerCacheKey);
        _topicKeyStrategy.GetTopicKey<string>().Returns(_topicKey);
        _changeTokenFactory = _fixture.Freeze<IChangeTokenFactory>();
        _memoryCache = _fixture.Freeze<IMemoryCache>();
        _innerCache = _fixture.Freeze<IHashCache>();
        _clock = new SystemClock();

        _options = new()
        {
            DefaultExpiration = TimeSpan.FromMinutes(10),
            EntryFactory = new TestCacheEntryFactory(),
            CacheKeyStrategy = _cacheKeyStrategy,
            TopicKeyStrategy = _topicKeyStrategy,
        };
        _topicFactory = _fixture.Freeze<ITopicFactory>();
        _topicProvider = _fixture.Freeze<ITopicProvider>();
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
}
