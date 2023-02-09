using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using UiPath.Platform.Caching.Telemetry;
using UiPath.Platform.Caching.Tests.Broadcast;

namespace UiPath.Platform.Caching.Tests.Hybrid;

public class HybridRegionCacheTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    private IRegionCache _innerCache = default!;
    private ICachingTelemetryProvider _cachingTelemetryProvider = default!;
    private ITelemetryOperation _telemetryOperation = default!;
    private IChangeTokenFactory _changeTokenFactory = default!;
    private IChannelPublisher<IClearCacheEvent> _channelPublisher = default!;
    private IChannelResolver _channelResolver = default!;
    private IMemoryCache _memoryCache = default!;
    private ISystemClock _clock = default!;
    private IEventFormatterProxy<IClearCacheEvent> _formatter = default!;
    private IClearCacheEventFactory _cacheEventFactory = default!;
    private HybridCacheOptions _hybridCacheOptions = default!;

    private HybridRegionCache? _sut = null;
    private HybridRegionCache Sut => _sut ??= _fixture.Create<HybridRegionCache>();

    [Fact]
    public async Task Get_data_from_inner_cache()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));

        var actual = await Sut.GetAsync<string>(region, CancellationToken.None);
        _changeTokenFactory.Received(1).Create(_hybridCacheOptions.ChannelPrefix, Arg.Any<string>(), Arg.Any<Uri?>());
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Get_unknown_region()
    {
        Region region = _fixture.Create<string>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = null
        };
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));

        var actual = await Sut.GetAsync<string>(region, _fixture.CreateMany<string>().ToArray(), CancellationToken.None);
        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_cache_entry()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));

        var actual = await Sut.GetCacheEntryAsync<string>(region, CancellationToken.None);
        actual.Should().NotBeNull();
        actual.Value.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Get_known_item()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var key = expected.Keys.First();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));

        var actual = await Sut.GetItemAsync<string?>(region, key, CancellationToken.None);
        _changeTokenFactory.Received(1).Create(_hybridCacheOptions.ChannelPrefix, Arg.Any<string>(), Arg.Any<Uri?>());
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
        actual.Should().Be(expected[key]);
    }

    [Fact]
    public async Task Get_item_unknown_key()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));

        var actual = await Sut.GetItemAsync<string?>(region, _fixture.Create<string>(), CancellationToken.None);
        _changeTokenFactory.Received(1).Create(_hybridCacheOptions.ChannelPrefix, Arg.Any<string>(), Arg.Any<Uri?>());
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Get_item_unknown_key_unknown_region()
    {
        Region region = _fixture.Create<string>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = null
        };
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));

        var actual = await Sut.GetItemAsync<string?>(region, _fixture.Create<string>(), CancellationToken.None);
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Get_data_from_memory_cache()
    {
        Region region = _fixture.Create<string>();
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
        var actual = await Sut.GetAsync<string>(region, CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
        await _innerCache.DidNotReceive().GetCacheEntryAsync<string>(region, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrAdd_data_from_inner_cache_timespan()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        var generatorExpected = _fixture.Create<IDictionary<string, string?>>();
        var generatorWasCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorWasCalled = true;
            return Task.FromResult(generatorExpected);
        };
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));

        var actual = await Sut.GetOrAddAsync(region, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeFalse();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_data_from_inner_cache_datetime()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        var generatorExpected = _fixture.Create<IDictionary<string, string?>>();
        var generatorWasCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorWasCalled = true;
            return Task.FromResult(generatorExpected);
        };
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));

        var actual = await Sut.GetOrAddAsync(region, generator, _fixture.Create<DateTimeOffset>(), CancellationToken.None);
        generatorWasCalled.Should().BeFalse();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_data_from_inner_cache_no_expiration()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        var generatorExpected = _fixture.Create<IDictionary<string, string?>>();
        var generatorWasCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorWasCalled = true;
            return Task.FromResult(generatorExpected);
        };
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));

        var actual = await Sut.GetOrAddAsync(region, generator, CancellationToken.None);
        generatorWasCalled.Should().BeFalse();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Dispose_can_be_called()
    {
        Sut.Dispose();
    }

    [Fact]
    public async Task GetOrAdd_data_from_generator()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        var generatorExpected = _fixture.Create<IDictionary<string, string?>>();
        var generatorWasCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorWasCalled = true;
            return Task.FromResult(generatorExpected);
        };
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(new TestCacheEntry<IDictionary<string, string?>>()));
        var actual = await Sut.GetOrAddAsync(region, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeTrue();
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
        await _innerCache.Received(1).SetAsync(region, Arg.Any<IDictionary<string, string?>>(), Arg.Any<RegionCacheEntryOptions>(), Arg.Any<CancellationToken>());
        actual.Should().BeEquivalentTo(generatorExpected);
    }

    [Fact]
    public async Task GetOrAdd_data_from_generator_default()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        IDictionary<string, string?> generatorExpected = new Dictionary<string, string?>();
        var generatorWasCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorWasCalled = true;
            return Task.FromResult(generatorExpected);
        };
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(new TestCacheEntry<IDictionary<string, string?>>()));

        var actual = await Sut.GetOrAddAsync(region, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeTrue();
        _memoryCache.DidNotReceive().CreateEntry(region);
        await _innerCache.DidNotReceive().SetAsync(region, Arg.Any<IDictionary<string, string?>>(), Arg.Any<RegionCacheEntryOptions>(), Arg.Any<CancellationToken>());
        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_default_value()
    {
        Region region = _fixture.Create<string>();
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(new TestCacheEntry<IDictionary<string, string?>>()));

        await Sut.SetAsync(region, new Dictionary<string, string?>(), _fixture.Create<TimeSpan>(), CancellationToken.None);
        _memoryCache.Received(1).Remove(Arg.Any<object>());
        await _innerCache.Received(1).RemoveAsync<string>(region, Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_region_no_expiration()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(new TestCacheEntry<IDictionary<string, string?>> { Value = expected }));

        await Sut.SetAsync(region, expected, CancellationToken.None);
        _memoryCache.DidNotReceive().Remove(Arg.Any<object>());
        await _innerCache.DidNotReceive().RemoveAsync<string>(region, Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
        await _innerCache.Received(1).SetAsync(region, Arg.Any<IDictionary<string, string?>>(), Arg.Any<RegionCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_value()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };

        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));

        var actual = await Sut.SetAsync(region, expected, _fixture.Create<TimeSpan>(), CancellationToken.None);
        _memoryCache.DidNotReceive().Remove(Arg.Any<object>());
        await _innerCache.DidNotReceive().RemoveAsync<string>(region, Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
        await _innerCache.Received(1).SetAsync(region, Arg.Any<IDictionary<string, string?>>(), Arg.Any<RegionCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_value_with_options()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        var options = new RegionCacheEntryOptions(default, _fixture.Create<TimeSpan>(), RegionCacheSetOption.KeyReplace, _fixture.Create<IDictionary<string, string?>>());
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));

        var actual = await Sut.SetAsync(region, expected, options, CancellationToken.None);
        _memoryCache.DidNotReceive().Remove(Arg.Any<object>());
        await _innerCache.DidNotReceive().RemoveAsync<string>(region, Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
        await _innerCache.Received(1).SetAsync(region, Arg.Any<IDictionary<string, string?>>(), Arg.Any<RegionCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_empty_value_with_options()
    {
        Region region = _fixture.Create<string>();
        IDictionary<string, string?> expected = new Dictionary<string, string?>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        var options = new RegionCacheEntryOptions(default, _fixture.Create<TimeSpan>(), RegionCacheSetOption.KeyReplace, _fixture.Create<IDictionary<string, string?>>());
        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));

        var actual = await Sut.SetAsync(region, expected, options, CancellationToken.None);
        _memoryCache.Received(1).Remove(Arg.Any<object>());
        await _innerCache.Received(1).RemoveAsync<string>(region, Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
        _memoryCache.DidNotReceive().CreateEntry(Arg.Any<object>());
        await _innerCache.DidNotReceive().SetAsync(region, Arg.Any<IDictionary<string, string?>>(), Arg.Any<RegionCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_value_inner_cache_throw_exception()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        _innerCache.SetAsync(region, Arg.Any<IDictionary<string, string?>>(), Arg.Any<RegionCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync<Exception>();

        var actual = await Sut.SetAsync(region, expected, _fixture.Create<TimeSpan>(), CancellationToken.None);
        actual.Should().BeFalse();

        actual = await Sut.SetAsync(region, expected, _fixture.Create<DateTimeOffset>(), CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_default_value_error()
    {
        Region region = _fixture.Create<string>();
        var expected = new Dictionary<string, string?>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };

        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));
        _channelPublisher.PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>())
            .Throws<Exception>();
        var actual = await Sut.RemoveAsync<string>(region, CancellationToken.None);
        _memoryCache.Received(1).Remove(Arg.Any<object>());
        await _innerCache.Received(1).RemoveAsync<string>(region, Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_default_value()
    {
        Region region = _fixture.Create<string>();
        var expected = new Dictionary<string, string?>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };

        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));
        var actual = await Sut.RemoveAsync<string>(region, CancellationToken.None);
        _memoryCache.Received(1).Remove(Arg.Any<object>());
        await _innerCache.Received(1).RemoveAsync<string>(region, Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
        actual.Should().BeTrue();
    }

    [Fact]
    public async Task Remove_evict_active_token()
    {
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = new SystemClock(),
        }));
        _memoryCache = memoryCache;

        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };

        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_hybridCacheOptions.ChannelPrefix, Arg.Is<string>(x => x.Contains(region, StringComparison.InvariantCultureIgnoreCase)), Arg.Any<Uri?>())
            .Returns(c => token);
        var sut = new HybridRegionCache(_innerCache, (opt) => memoryCache, _changeTokenFactory, _channelPublisher, _channelResolver, _cacheEventFactory, _cachingTelemetryProvider, Options.Create(_hybridCacheOptions), _fixture.Freeze<ILogger<HybridRegionCache>>());
        var actual = await sut.GetAsync<string>(region, CancellationToken.None);
        await sut.RemoveAsync<string>(region);
        await token.AssertIsDisposed();
    }

    [Fact]
    public async Task Remove_evict_active_token_callback()
    {
        var clock = new SystemClock();
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = clock
        }));
        _memoryCache = memoryCache;

        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };

        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));
        _innerCache.ExpireTimeAsync(region, CancellationToken.None)
               .ReturnsForAnyArgs(Task.FromResult((DateTimeOffset?)clock.UtcNow.AddDays(1)));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_hybridCacheOptions.ChannelPrefix, Arg.Any<string>(), Arg.Any<Uri?>())
            .ReturnsForAnyArgs(c => token);
        var sut = new HybridRegionCache(_innerCache, (opt) => memoryCache, _changeTokenFactory, _channelPublisher, _channelResolver, _cacheEventFactory, _cachingTelemetryProvider, Options.Create(_hybridCacheOptions), _fixture.Freeze<ILogger<HybridRegionCache>>());
        var actual = await sut.GetAsync<string>(region, CancellationToken.None);
        token.HasChanged = true;
        token.InvokeCallbacks();
        memoryCache.TryGetValue(region, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_ExtendedProperties_callback()
    {
        var clock = new SystemClock();
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = clock
        }));
        _memoryCache = memoryCache;

        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedExtendedProperties = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected,
            ExtendedProperties = _fixture.Create<IDictionary<string, string?>>()
        };

        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));
        _innerCache.GetExtendedPropertiesAsync(region, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IDictionary<string, string?>?>(expectedExtendedProperties));
        _innerCache.ExpireTimeAsync(region, CancellationToken.None)
               .ReturnsForAnyArgs(Task.FromResult((DateTimeOffset?)clock.UtcNow.AddDays(1)));
        TestChangeToken? token = default;
        _changeTokenFactory.Create(_hybridCacheOptions.ChannelPrefix, Arg.Any<string>(), Arg.Any<Uri?>())
            .ReturnsForAnyArgs(c =>
            {
                token = new TestChangeToken
                {
                    ActiveChangeCallbacks = true,
                    HasChanged = false,
                    ExtendedPropertiesHasChanged = false
                };
                return token;
            });
        var sut = new HybridRegionCache(_innerCache, (opt) => memoryCache, _changeTokenFactory, _channelPublisher, _channelResolver, _cacheEventFactory, _cachingTelemetryProvider, Options.Create(_hybridCacheOptions), _fixture.Freeze<ILogger<HybridRegionCache>>());
        var actual = await sut.GetAsync<string>(region, CancellationToken.None);
        token.Should().NotBeNull();
        memoryCache.TryGetValue(region, out _).Should().BeTrue();
        token!.HasChanged = true;
        token.ExtendedPropertiesHasChanged = true;
        token.InvokeCallbacks();
        memoryCache.TryGetValue(region, out _).Should().BeTrue();
        //cacheEntry!.ExtendedProperties.Should().BeEquivalentTo(expectedExtendedProperties);
    }

    [Fact]
    public async Task Refresh_ExtendedProperties_callback_no_cache_props()
    {
        var clock = new SystemClock();
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = clock
        }));
        _memoryCache = memoryCache;

        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected,
            ExtendedProperties = _fixture.Create<IDictionary<string, string?>>()
        };

        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));
        _innerCache.GetExtendedPropertiesAsync(region, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IDictionary<string, string?>?>(default));
        _innerCache.ExpireTimeAsync(region, CancellationToken.None)
               .ReturnsForAnyArgs(Task.FromResult((DateTimeOffset?)clock.UtcNow.AddDays(1)));
        TestChangeToken? token = default;
        _changeTokenFactory.Create(_hybridCacheOptions.ChannelPrefix, Arg.Any<string>(), Arg.Any<Uri?>())
            .ReturnsForAnyArgs(c =>
            {
                token = new TestChangeToken
                {
                    ActiveChangeCallbacks = true,
                    HasChanged = false,
                    ExtendedPropertiesHasChanged = false
                };
                return token;
            });
        var sut = new HybridRegionCache(_innerCache, (opt) => memoryCache, _changeTokenFactory, _channelPublisher, _channelResolver, _cacheEventFactory, _cachingTelemetryProvider, Options.Create(_hybridCacheOptions), _fixture.Freeze<ILogger<HybridRegionCache>>());
        var actual = await sut.GetAsync<string>(region, CancellationToken.None);
        token.Should().NotBeNull();
        memoryCache.TryGetValue(region, out _).Should().BeTrue();
        token!.HasChanged = true;
        token.ExtendedPropertiesHasChanged = true;
        token.InvokeCallbacks();
        memoryCache.TryGetValue(region, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_ExtendedProperties_callback_cache_throw_exception()
    {
        var clock = new SystemClock();
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = clock
        }));
        _memoryCache = memoryCache;

        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected,
            ExtendedProperties = _fixture.Create<IDictionary<string, string?>>()
        };

        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));
        _innerCache.GetExtendedPropertiesAsync(region, Arg.Any<CancellationToken>())
            .Throws(new Exception());
        _innerCache.ExpireTimeAsync(region, CancellationToken.None)
               .ReturnsForAnyArgs(Task.FromResult((DateTimeOffset?)clock.UtcNow.AddDays(1)));
        TestChangeToken? token = default;
        _changeTokenFactory.Create(_hybridCacheOptions.ChannelPrefix, Arg.Any<string>(), Arg.Any<Uri?>())
            .ReturnsForAnyArgs(c =>
            {
                token = new TestChangeToken
                {
                    ActiveChangeCallbacks = true,
                    HasChanged = false,
                    ExtendedPropertiesHasChanged = false
                };
                return token;
            });
        var sut = new HybridRegionCache(_innerCache, (opt) => memoryCache, _changeTokenFactory, _channelPublisher, _channelResolver, _cacheEventFactory, _cachingTelemetryProvider, Options.Create(_hybridCacheOptions), _fixture.Freeze<ILogger<HybridRegionCache>>());
        var actual = await sut.GetAsync<string>(region, CancellationToken.None);
        token.Should().NotBeNull();
        memoryCache.TryGetValue(region, out _).Should().BeTrue();
        token!.HasChanged = true;
        token.ExtendedPropertiesHasChanged = true;
        token.InvokeCallbacks();
        memoryCache.TryGetValue(region, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_evict_token_non_active()
    {
        var clock = _fixture.Freeze<ISystemClock>();
        var now = DateTimeOffset.UtcNow;
        clock.UtcNow.Returns(now);
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = clock,
        }));
        _memoryCache = memoryCache;

        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };

        _innerCache.GetCacheEntryAsync<string>(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<ICacheEntry<IDictionary<string, string?>>>(expectedCacheEntry));
        _innerCache.ExpireTimeAsync(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult((DateTimeOffset?)now.AddDays(1)));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = false,
            HasChanged = false
        };
        _changeTokenFactory.Create(_hybridCacheOptions.ChannelPrefix, Arg.Any<string>(), Arg.Any<Uri?>())
            .ReturnsForAnyArgs(c => token);
        var sut = new HybridRegionCache(_innerCache, (opt) => memoryCache, _changeTokenFactory, _channelPublisher, _channelResolver, _cacheEventFactory, _cachingTelemetryProvider, Options.Create(new HybridCacheOptions
        {
            Clock = clock,
        }), _fixture.Freeze<ILogger<HybridRegionCache>>());
        var actual = await sut.GetAsync<string>(region, CancellationToken.None);
        token.Callbacks.Should().BeEmpty();
        memoryCache.TryGetValue(region, out _).Should().BeTrue();
        token.HasChanged = true;
        memoryCache.TryGetValue(region, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_value_TimeSpan()
    {
        Region region = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan?>();
        await Sut.RefreshAsync<string>(region, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(region);
        await _innerCache.Received(1).RefreshAsync<string>(region, Arg.Any<RegionCacheEntryOptions>(), Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_value_no_expiration()
    {
        Region region = _fixture.Create<string>();
        await Sut.RefreshAsync<string>(region, CancellationToken.None);
        _memoryCache.Received(1).Remove(region);
        await _innerCache.Received(1).RefreshAsync<string>(region, Arg.Any<RegionCacheEntryOptions>(), Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_value_DateTimeOffset()
    {
        Region region = _fixture.Create<string>();
        var expiration = DateTimeOffset.UtcNow.AddDays(1);
        await Sut.RefreshAsync<string>(region, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(region);
        await _innerCache.Received(1).RefreshAsync<string>(region, Arg.Any<RegionCacheEntryOptions>(), Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_inner_cache_exception_timespan()
    {
        Region region = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan?>();
        _innerCache.RefreshAsync<string>(region, Arg.Any<RegionCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync<Exception>();
        await Sut.RefreshAsync<string>(region, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(region);
        await _innerCache.Received(1).RefreshAsync<string>(region, Arg.Any<RegionCacheEntryOptions>(), Arg.Any<CancellationToken>());
        await _channelPublisher.DidNotReceive().PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_inner_cache_exception_DateTimeOffset()
    {
        Region region = _fixture.Create<string>();
        var expiration = DateTimeOffset.UtcNow.AddDays(1);
        _innerCache.RefreshAsync<string>(region, Arg.Any<RegionCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync<Exception>();
        await Sut.RefreshAsync<string>(region, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(region);
        await _innerCache.Received(1).RefreshAsync<string>(region, Arg.Any<RegionCacheEntryOptions>(), Arg.Any<CancellationToken>());
        await _channelPublisher.DidNotReceive().PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Contains_in_inner_cache()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<bool>();
        var memoryCacheCalled = false;
        _memoryCache.TryGetValue(region, out Arg.Any<object?>())
            .Returns(x =>
            {
                memoryCacheCalled = true;
                return false;
            });
        _innerCache.ContainsAsync(region, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult(expected));
        var actual = await Sut.ContainsAsync(region, CancellationToken.None);
        actual.Should().Be(expected);
        memoryCacheCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_in_memory_cache()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<bool>();
        var memoryCacheCalled = false;
        _memoryCache.TryGetValue(region, out Arg.Any<object?>())
            .Returns(x =>
            {
                memoryCacheCalled = true;
                return true;
            });
        var actual = await Sut.ContainsAsync(region, CancellationToken.None);
        await _innerCache.DidNotReceive().ContainsAsync(region, CancellationToken.None);
        actual.Should().Be(expected);
        memoryCacheCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_in_inner_cache_exception()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<bool>();
        var memoryCacheCalled = false;
        _memoryCache.TryGetValue(region, out Arg.Any<object?>())
            .Returns(x =>
            {
                memoryCacheCalled = true;
                return false;
            });
        _innerCache.ContainsAsync(region, CancellationToken.None)
            .ThrowsAsync<Exception>();
        var actual = await Sut.ContainsAsync(region, CancellationToken.None);
        actual.Should().Be(false);
        memoryCacheCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Key()
    {
        Region region = _fixture.Create<string>();
        var logger = _fixture.Freeze<ILogger<HybridRegionCache>>();
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock
        }));
        _memoryCache = memoryCache;
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_hybridCacheOptions.ChannelPrefix, Arg.Any<string>(), Arg.Any<Uri?>())
            .Returns(c => token);

        var expiration = _clock.UtcNow.AddYears(1);
        var sut = new HybridRegionCache(_innerCache, (opt) => memoryCache, _changeTokenFactory, _channelPublisher, _channelResolver, _cacheEventFactory, _cachingTelemetryProvider, Options.Create(_hybridCacheOptions), _fixture.Freeze<ILogger<HybridRegionCache>>());
        var values = _fixture.Create<IDictionary<string, int>>();
        await sut.SetAsync(region, values, expiration, CancellationToken.None);
        var actual = await sut.ExpireTimeAsync(region);
        expiration.Should().Be(actual);
    }

    [Fact]
    public async Task Read_ExpireTimeToLive_For_Key()
    {
        var clock = new SystemClock();
        Region region = _fixture.Create<string>();
        var logger = _fixture.Freeze<ILogger<HybridRegionCache>>();
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = clock
        }));
        _memoryCache = memoryCache;
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_hybridCacheOptions.ChannelPrefix, Arg.Any<string>(), Arg.Any<Uri?>())
            .Returns(c => token);

        var expiration = TimeSpan.FromDays(1);
        var values = _fixture.Create<IDictionary<string, int>>();
        var sut = new HybridRegionCache(_innerCache, (opt) => memoryCache, _changeTokenFactory, _channelPublisher, _channelResolver, _cacheEventFactory, _cachingTelemetryProvider, Options.Create(_hybridCacheOptions), _fixture.Freeze<ILogger<HybridRegionCache>>());
        await sut.SetAsync(region, values, expiration, CancellationToken.None);
        var actual = await sut.TimeToLiveAsync(region);
        expiration.Should().BeCloseTo(actual.GetValueOrDefault(), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task GetExtendedProperties_from_memory()
    {
        var clock = new SystemClock();
        Region region = _fixture.Create<string>();
        var logger = _fixture.Freeze<ILogger<HybridRegionCache>>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = _fixture.Create<IDictionary<string, string?>>(),
            ExtendedProperties = expected
        };


        _memoryCache.TryGetValue(Arg.Any<object>(), out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = expectedCacheEntry;
                return true;
            });

        var values = _fixture.Create<IDictionary<string, int>>();
        var actual = await Sut.GetExtendedPropertiesAsync(region, CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetExtendedProperties_from_innerCache()
    {
        var clock = new SystemClock();
        Region region = _fixture.Create<string>();
        var logger = _fixture.Freeze<ILogger<HybridRegionCache>>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = _fixture.Create<IDictionary<string, string?>>(),
            ExtendedProperties = expected
        };

        _innerCache.GetExtendedPropertiesAsync(region, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(expected);
        _memoryCache.TryGetValue(Arg.Any<object>(), out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = _fixture.Create<IDictionary<string, string?>>();
                return false;
            });

        var values = _fixture.Create<IDictionary<string, int>>();
        var actual = await Sut.GetExtendedPropertiesAsync(region, CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task SetExtendedProperties_works_as_exptected()
    {
        var clock = new SystemClock();
        Region region = _fixture.Create<string>();
        var logger = _fixture.Freeze<ILogger<HybridRegionCache>>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = _fixture.Create<IDictionary<string, string?>>(),
            ExtendedProperties = expected
        };

        _innerCache.GetExtendedPropertiesAsync(region, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(expected);
        _memoryCache.TryGetValue(Arg.Any<object>(), out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = _fixture.Create<IDictionary<string, string?>>();
                return false;
            });

        var values = _fixture.Create<IDictionary<string, int>>();
        await Sut.SetExtendedPropertiesAsync<string>(region, expected, CancellationToken.None);
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetExtendedProperties_works_exception()
    {
        var clock = new SystemClock();
        Region region = _fixture.Create<string>();
        var logger = _fixture.Freeze<ILogger<HybridRegionCache>>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = _fixture.Create<IDictionary<string, string?>>(),
            ExtendedProperties = expected
        };

        _innerCache.SetExtendedPropertiesAsync<string>(region, Arg.Any<IDictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Throws(new Exception());
        _memoryCache.TryGetValue(Arg.Any<object>(), out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = _fixture.Create<IDictionary<string, string?>>();
                return false;
            });

        var values = _fixture.Create<IDictionary<string, int>>();
        var response = await Sut.SetExtendedPropertiesAsync<string>(region, expected, CancellationToken.None);
        response.Should().BeFalse();
        await _channelPublisher.DidNotReceive().PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_no_inner_cache_expire_time_use_max()
    {
        var key = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            return Task.FromResult(expected);
        };

        Region region = _fixture.Create<string>();

        _innerCache.ExpireTimeAsync(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult(default(DateTimeOffset?)));

        var cacheEntry = _fixture.Freeze<Microsoft.Extensions.Caching.Memory.ICacheEntry>();
        _memoryCache.CreateEntry(Arg.Any<object>())
            .ReturnsForAnyArgs(cacheEntry);

        _hybridCacheOptions.DefaultExpiration = null;
        _ = await Sut.GetOrAddAsync(key, generator, default(DateTimeOffset?), CancellationToken.None);
        cacheEntry.AbsoluteExpiration.Should().Be(DateTimeOffset.MaxValue);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _changeTokenFactory = _fixture.Freeze<IChangeTokenFactory>();
        _channelPublisher = _fixture.Freeze<IChannelPublisher<IClearCacheEvent>>();
        _channelResolver = _fixture.Freeze<IChannelResolver>();
        _memoryCache = _fixture.Freeze<IMemoryCache>();
        _innerCache = _fixture.Freeze<IRegionCache>();
        _cachingTelemetryProvider = _fixture.Freeze<ICachingTelemetryProvider>();
        _telemetryOperation = _fixture.Freeze<ITelemetryOperation>();
        _clock = new SystemClock();
        _hybridCacheOptions = new()
        {
            ChannelPrefix = "test",
            DefaultExpiration = TimeSpan.FromMinutes(10),
            EntryFactory = new TestCacheEntryFactory(),
            SourceUri = null
        };
        _channelResolver.GetFor<object>(Arg.Any<string>()).Returns((Channel)_hybridCacheOptions.ChannelPrefix);
        _channelResolver.GetFor(Arg.Any<Type>(), Arg.Any<string>()).Returns((Channel)_hybridCacheOptions.ChannelPrefix);
        _fixture.Inject<Func<IMemoryCache>>(() => _memoryCache);
        _fixture.Inject(Options.Create(_hybridCacheOptions));
        _formatter = new CacheClearEventFormatterProxy();
        _fixture.Inject(_formatter);
        _cacheEventFactory = _fixture.Freeze<IClearCacheEventFactory>();
        _cacheEventFactory.Create(Arg.Any<ClearCacheEventData>(), Arg.Any<Uri?>(), Arg.Any<string?>())
            .Returns(c =>
                new TestClearCacheEvent
                {
                    Id = c.Arg<string?>(),
                    Data = c.Arg<ClearCacheEventData>(),
                    Source = c.Arg<Uri?>()
                }
            );
        return Task.CompletedTask;
    }
}
