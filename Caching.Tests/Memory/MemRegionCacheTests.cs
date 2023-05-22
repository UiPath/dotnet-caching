using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using UiPath.Platform.Caching.Memory;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Tests.Memory;

public class MemRegionCacheTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    private ICachingTelemetryProvider _cachingTelemetryProvider = default!;
    private IChangeTokenFactory _changeTokenFactory = default!;
    private IMemoryCache _memoryCache = default!;
    private ISystemClock _clock = default!;
    private MemCacheOptions _cacheOptions = default!;
    private TestChangeToken _token = new();

    private MemRegionCache? _sut = null;
    private MemRegionCache Sut => _sut ??= _fixture.Create<MemRegionCache>();


    [Fact]
    public async Task Get_unknown_region()
    {
        Region region = _fixture.Create<string>();
        var actual = await Sut.GetAsync<string>(region, _fixture.CreateMany<string>().ToArray(), CancellationToken.None);
        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_cache_entry()
    {
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = new SystemClock(),
        }));
        _memoryCache = memoryCache;

        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };

        var sut = new MemRegionCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemRegionCache>>());

        await Sut.SetAsync(region, expected, CancellationToken.None);
        var actual = await sut.GetCacheEntryAsync<string>(region, CancellationToken.None);
        actual.Should().NotBeNull();
        actual.Value.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Get_known_item()
    {
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = new SystemClock(),
        }));
        _memoryCache = memoryCache;

        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };

        var sut = new MemRegionCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemRegionCache>>());

        var key = expected.Keys.First();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        await Sut.SetAsync(region, expected, CancellationToken.None);
        var actual = await Sut.GetItemAsync<string?>(region, key, CancellationToken.None);
        actual.Should().Be(expected[key]);
    }

    [Fact]
    public async Task Get_item_unknown_key()
    {
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = new SystemClock(),
        }));
        _memoryCache = memoryCache;

        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        var sut = new MemRegionCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemRegionCache>>());

        await Sut.SetAsync(region, expected, CancellationToken.None);
        var actual = await Sut.GetItemAsync<string?>(region, _fixture.Create<string>(), CancellationToken.None);
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Get_item_unknown_key_unknown_region()
    {
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = new SystemClock(),
        }));
        _memoryCache = memoryCache;

        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        var sut = new MemRegionCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemRegionCache>>());

        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        await Sut.SetAsync(region, expected, CancellationToken.None);
        Region region2 = _fixture.Create<string>();
        var actual = await Sut.GetItemAsync<string?>(region2, _fixture.Create<string>(), CancellationToken.None);
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
    }

    [Fact]
    public async Task GetOrAdd_data_from_inner_cache_timespan()
    {
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = new SystemClock(),
        }));
        _memoryCache = memoryCache;

        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };

        var sut = new MemRegionCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemRegionCache>>());

        await Sut.SetAsync(region, expected, CancellationToken.None);
        var generatorExpected = _fixture.Create<IDictionary<string, string?>>();
        var generatorWasCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorWasCalled = true;
            return Task.FromResult(generatorExpected);
        };

        var actual = await Sut.GetOrAddAsync(region, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeFalse();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_data_from_inner_cache_datetime()
    {
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = new SystemClock(),
        }));
        _memoryCache = memoryCache;

        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        var sut = new MemRegionCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemRegionCache>>());

        await Sut.SetAsync(region, expected, CancellationToken.None);
        var generatorExpected = _fixture.Create<IDictionary<string, string?>>();
        var generatorWasCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorWasCalled = true;
            return Task.FromResult(generatorExpected);
        };

        var actual = await Sut.GetOrAddAsync(region, generator, _fixture.Create<DateTimeOffset>(), CancellationToken.None);
        generatorWasCalled.Should().BeFalse();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_data_from_inner_cache_no_expiration()
    {
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = new SystemClock(),
        }));
        _memoryCache = memoryCache;

        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        var sut = new MemRegionCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemRegionCache>>());

        await Sut.SetAsync(region, expected, CancellationToken.None);
        var generatorExpected = _fixture.Create<IDictionary<string, string?>>();
        var generatorWasCalled = false;
        Func<Task<IDictionary<string, string?>>> generator = () =>
        {
            generatorWasCalled = true;
            return Task.FromResult(generatorExpected);
        };

        var actual = await Sut.GetOrAddAsync(region, generator, CancellationToken.None);
        generatorWasCalled.Should().BeFalse();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Dispose_can_be_called()
    {
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
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
        var actual = await Sut.GetOrAddAsync(region, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeTrue();
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
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

        var actual = await Sut.GetOrAddAsync(region, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeTrue();
        _memoryCache.DidNotReceive().CreateEntry(region);
        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_default_value()
    {
        Region region = _fixture.Create<string>();

        await Sut.SetAsync(region, new Dictionary<string, string?>(), _fixture.Create<TimeSpan>(), CancellationToken.None);
        _memoryCache.Received(1).Remove(Arg.Any<object>());
    }

    [Fact]
    public async Task Set_region_no_expiration()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();

        await Sut.SetAsync(region, expected, CancellationToken.None);
        _memoryCache.DidNotReceive().Remove(Arg.Any<object>());
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


        var actual = await Sut.SetAsync(region, expected, _fixture.Create<TimeSpan>(), CancellationToken.None);
        _memoryCache.DidNotReceive().Remove(Arg.Any<object>());
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
    }

    [Fact]
    public async Task Set_value_with_options()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var options = new RegionCacheEntryOptions(default, _fixture.Create<TimeSpan>(), _fixture.Create<IDictionary<string, string?>>());

        var actual = await Sut.SetAsync(region, expected, options, CancellationToken.None);
        _memoryCache.DidNotReceive().Remove(Arg.Any<object>());
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
    }

    [Fact]
    public async Task Set_empty_value_with_options()
    {
        Region region = _fixture.Create<string>();
        IDictionary<string, string?> expected = new Dictionary<string, string?>();
        var options = new RegionCacheEntryOptions(default, _fixture.Create<TimeSpan>(), _fixture.Create<IDictionary<string, string?>>());

        var actual = await Sut.SetAsync(region, expected, options, CancellationToken.None);
        _memoryCache.Received(1).Remove(Arg.Any<object>());
        _memoryCache.DidNotReceive().CreateEntry(Arg.Any<object>());
    }

    [Fact]
    public async Task Remove_default_value()
    {
        Region region = _fixture.Create<string>();
        var actual = await Sut.RemoveAsync<string>(region, CancellationToken.None);
        _memoryCache.Received(1).Remove(Arg.Any<object>());
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

        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        var sut = new MemRegionCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemRegionCache>>());
        await sut.SetAsync(region, expected, CancellationToken.None);
        var actual = await sut.GetAsync<string>(region, CancellationToken.None);
        await sut.RemoveAsync<string>(region);
        await _token.AssertIsDisposed();
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

        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        var sut = new MemRegionCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemRegionCache>>());
        var actual = await sut.GetAsync<string>(region, CancellationToken.None);
        _token.HasChanged = true;
        _token.InvokeCallbacks();
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

        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = false,
            HasChanged = false
        };

        var sut = new MemRegionCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(new MemCacheOptions
        {
            Clock = clock,
        }), _fixture.Freeze<ILogger<MemRegionCache>>());
        var options = new RegionCacheEntryOptions(default, default, default);
        await sut.SetAsync(region, expected, options, CancellationToken.None);
        var actual = await sut.GetAsync<string>(region, CancellationToken.None);
        _token.Callbacks.Should().BeEmpty();
        memoryCache.TryGetValue(region, out _).Should().BeTrue();
        _token.HasChanged = true;
        memoryCache.TryGetValue(region, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_value_TimeSpan()
    {
        Region region = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan?>();
        var clock = new SystemClock();
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = clock
        }));
        _memoryCache = memoryCache;

        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedExtendedProperties = _fixture.Create<IDictionary<string, string?>>();

        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false,
            ExtendedPropertiesHasChanged = false,
        };
        var sut = new MemRegionCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemRegionCache>>());
        await sut.SetAsync(region, expected, CancellationToken.None);
        var actual = await sut.RefreshAsync<string>(region, expiration, CancellationToken.None);
        actual.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_value_no_expiration()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        _memoryCache.TryGetValue(region, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = expectedCacheEntry;
                return true;
            });
        await Sut.RefreshAsync<string>(region, CancellationToken.None);
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
    }

    [Fact]
    public async Task Refresh_value_DateTimeOffset()
    {
        Region region = _fixture.Create<string>();
        var expected = _fixture.Create<IDictionary<string, string?>>();
        var expectedCacheEntry = new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = expected
        };
        _memoryCache.TryGetValue(region, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = expectedCacheEntry;
                return true;
            });
        var expiration = DateTimeOffset.UtcNow.AddDays(1);
        await Sut.RefreshAsync<string>(region, expiration, CancellationToken.None);
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Contains(bool expected)
    {
        Region region = _fixture.Create<string>();
        var memoryCacheCalled = false;
        _memoryCache.TryGetValue(region, out Arg.Any<object?>())
            .Returns(x =>
            {
                memoryCacheCalled = true;
                return expected;
            });
        var actual = await Sut.ContainsAsync(region, CancellationToken.None);
        actual.Should().Be(expected);
        memoryCacheCalled.Should().BeTrue();
    }


 
    [Fact]
    public async Task Read_ExpireTime_For_Key()
    {
        Region region = _fixture.Create<string>();
        var logger = _fixture.Freeze<ILogger<MemRegionCache>>();
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock
        }));
        _memoryCache = memoryCache;
        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };

        var expiration = _clock.UtcNow.AddYears(1);
        var sut = new MemRegionCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemRegionCache>>());
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
        var logger = _fixture.Freeze<ILogger<MemRegionCache>>();
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = clock
        }));
        _memoryCache = memoryCache;
        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };

        var expiration = TimeSpan.FromDays(1);
        var values = _fixture.Create<IDictionary<string, int>>();
        var sut = new MemRegionCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemRegionCache>>());
        await sut.SetAsync(region, values, expiration, CancellationToken.None);
        var actual = await sut.TimeToLiveAsync(region);
        expiration.Should().BeCloseTo(actual.GetValueOrDefault(), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task GetExtendedProperties_from_memory()
    {
        var clock = new SystemClock();
        Region region = _fixture.Create<string>();
        var logger = _fixture.Freeze<ILogger<MemRegionCache>>();
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
    public async Task GetExtendedProperties_from_unknown_key()
    {
        var clock = new SystemClock();
        Region region = _fixture.Create<string>();
        var logger = _fixture.Freeze<ILogger<MemRegionCache>>();
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
                return false;
            });

        var actual = await Sut.GetExtendedPropertiesAsync(region, CancellationToken.None);
        actual.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task SetExtendedProperties_works_as_exptected()
    {
        var clock = new SystemClock();
        Region region = _fixture.Create<string>();
        var logger = _fixture.Freeze<ILogger<MemRegionCache>>();
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
        Func<Task> act = () => Sut.SetExtendedPropertiesAsync<string>(region, expected, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetExtendedProperties_works_exception()
    {
        var clock = new SystemClock();
        Region region = _fixture.Create<string>();
        var logger = _fixture.Freeze<ILogger<MemRegionCache>>();
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
                return false;
            });

        var values = _fixture.Create<IDictionary<string, int>>();
        var response = await Sut.SetExtendedPropertiesAsync<string>(region, expected, CancellationToken.None);
        response.Should().BeFalse();
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

        var cacheEntry = _fixture.Freeze<Microsoft.Extensions.Caching.Memory.ICacheEntry>();
        _memoryCache.CreateEntry(Arg.Any<object>())
            .ReturnsForAnyArgs(cacheEntry);

        _cacheOptions.DefaultExpiration = null;
        _ = await Sut.GetOrAddAsync(key, generator, default(DateTimeOffset?), CancellationToken.None);
        cacheEntry.AbsoluteExpiration.Should().Be(DateTimeOffset.MaxValue);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _changeTokenFactory = new TestChangeTokenFactory((channel, key) => _token);
        _fixture.Inject(_changeTokenFactory);

        _memoryCache = _fixture.Freeze<IMemoryCache>();
        _cachingTelemetryProvider = _fixture.Freeze<ICachingTelemetryProvider>();
        _clock = new SystemClock();
        _cacheOptions = new()
        {
            DefaultExpiration = TimeSpan.FromMinutes(10),
            EntryFactory = new TestCacheEntryFactory(),
        };
        _fixture.Inject<Func<MemCacheOptions, IMemoryCache>>(x => _memoryCache);
        _fixture.Inject(Options.Create(_cacheOptions));
        return Task.CompletedTask;
    }
}
