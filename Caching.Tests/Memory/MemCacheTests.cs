using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using UiPath.Platform.Caching.Memory;
using UiPath.Platform.Caching.Telemetry;
using UiPath.Platform.Caching.Tests.Broadcast;

namespace UiPath.Platform.Caching.Tests.Memory;

public class MemCacheTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    private ICachingTelemetryProvider _cachingTelemetryProvider = default!;
    private IChangeTokenFactory _changeTokenFactory = default!;
    private IMemoryCache _memoryCache = default!;
    private ISystemClock _clock = default!;
    private IEventFormatterProxy<ICacheEvent> _formatter = default!;
    private MemCacheOptions _cacheOptions = default!;
    private TestChangeToken _token = new ();

    private MemCache? _sut = null;

    private MemCache Sut => _sut ??= _fixture.Create<MemCache>();


    [Fact]
    public async Task Get_data_from_cache()
    {
        var expected = _fixture.Create<string>();
        var key = _fixture.Create<string>();
        _memoryCache.TryGetValue(key, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string> { Value = expected };
                return true;
            });
        var actual = await Sut.GetAsync<string>(key, CancellationToken.None);
        actual.Should().Be(expected);
    }

    [Fact]
    public async Task Get_null_key()
    {
        string? ns = null;
        Func<Task> act = async () => await Sut.GetAsync<int>(ns!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetOrAdd_data_from_inner_cache()
    {
        var expected = _fixture.Create<string>();
        var key = _fixture.Create<string>();
        _memoryCache.TryGetValue(key, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string> { Value = expected };
                return true;
            });
        var generatorExpected = _fixture.Create<string>();
        var generatorWasCalled = false;
        Func<Task<string?>> generator = () =>
        {
            generatorWasCalled = true;
            return Task.FromResult((string?)generatorExpected);
        };

        var actual = await Sut.GetOrAddAsync(key, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeFalse();
        actual.Should().Be(expected);
    }

    [Fact]
    public async Task GetOrAdd_data_from_inner_cache_default_expiration()
    {
        var expected = _fixture.Create<string>();
        var key = _fixture.Create<string>();
        _memoryCache.TryGetValue(key, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string> { Value = expected };
                return true;
            });
        var generatorExpected = _fixture.Create<string>();
        var generatorWasCalled = false;
        Func<Task<string?>> generator = () =>
        {
            generatorWasCalled = true;
            return Task.FromResult((string?)generatorExpected);
        };

        var actual = await Sut.GetOrAddAsync(key, generator, CancellationToken.None);
        generatorWasCalled.Should().BeFalse();
        actual.Should().Be(expected);
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
        var key = _fixture.Create<string>();
        var generatorExpected = _fixture.Create<string>();
        var generatorWasCalled = false;
        Func<Task<string?>> generator = () =>
        {
            generatorWasCalled = true;
            return Task.FromResult((string?)generatorExpected);
        };

        var actual = await Sut.GetOrAddAsync(key, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeTrue();
        _memoryCache.Received(1).CreateEntry(key);
        actual.Should().Be(generatorExpected);
    }

    [Fact]
    public async Task GetOrAdd_data_from_generator_default()
    {
        var key = _fixture.Create<string>();
        var generatorExpected = _fixture.Create<string>();
        var generatorWasCalled = false;
        Func<Task<string?>> generator = () =>
        {
            generatorWasCalled = true;
            return Task.FromResult(default(string?));
        };

        var actual = await Sut.GetOrAddAsync(key, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeTrue();
        _memoryCache.Received(0).CreateEntry(key);
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Set_default_value()
    {
        var key = _fixture.Create<string>();

        var actual = await Sut.SetAsync(key, default(string), _fixture.Create<TimeSpan>(), CancellationToken.None);
        _memoryCache.Received(1).Remove(key);
    }

    [Fact]
    public async Task Set_default_value_default_expiration()
    {
        var key = _fixture.Create<string>();

        var actual = await Sut.SetAsync(key, default(string), CancellationToken.None);
        _memoryCache.Received(1).Remove(key);
    }

    [Fact]
    public async Task Set_value_value()
    {
        var key = _fixture.Create<string>();
        var value = _fixture.Create<string>();

        var actual = await Sut.SetAsync(key, value, _fixture.Create<TimeSpan>(), CancellationToken.None);
        _memoryCache.Received(0).Remove(key);
        _memoryCache.Received(1).CreateEntry(key);
    }


    [Fact]
    public async Task Remove_default_value()
    {
        var key = _fixture.Create<string>();
        var actual = await Sut.RemoveAsync<string>(key, CancellationToken.None);
        _memoryCache.Received(1).Remove(key);
        actual.Should().BeTrue();
    }

    [Fact]
    public async Task Remove_default_value_error()
    {
        var key = _fixture.Create<string>();
        var actual = await Sut.RemoveAsync<string>(key, CancellationToken.None);
        _memoryCache.Received(1).Remove(key);
        actual.Should().BeTrue();
    }

    [Fact]
    public async Task Remove_evict_active_token()
    {
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = new SystemClock(),
            CompactionPercentage = 0.3,
            ExpirationScanFrequency = TimeSpan.FromSeconds(2),
        }));
        _memoryCache = memoryCache;

        var key = _fixture.Create<string>();
        var expected = _fixture.Create<string>();
        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        var sut = new MemCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemCache>>());
        await sut.SetAsync(key, expected, CancellationToken.None);
        var actual = await sut.GetAsync<string>(key, CancellationToken.None);
        await sut.RemoveAsync<string>(key);
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

        var key = _fixture.Create<string>();
        var expected = _fixture.Create<string>();

        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        var sut = new MemCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemCache>>());
        await sut.SetAsync(key, expected, CancellationToken.None);
        var actual = await sut.GetAsync<string>(key, CancellationToken.None);
        actual.Should().Be(expected);
        _token.HasChanged = true;
        _token.InvokeCallbacks();
        memoryCache.TryGetValue(key, out _).Should().BeFalse();
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

        var key = _fixture.Create<string>();
        var expected = _fixture.Create<string>();

        _token = new TestChangeToken
        {
            ActiveChangeCallbacks = false,
            HasChanged = false
        };

        var sut = new MemCache((d) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(new MemCacheOptions
        {
            Clock = clock,
        }), _fixture.Freeze<ILogger<MemCache>>());

        await sut.SetAsync(key, expected, CancellationToken.None);
        var actual = await sut.GetAsync<string>(key, CancellationToken.None);
        actual.Should().Be(expected);
        _token.Callbacks.Should().BeEmpty();
        memoryCache.TryGetValue(key, out _).Should().BeTrue();
        _token.HasChanged = true;
        memoryCache.TryGetValue(key, out _).Should().BeFalse();
    }
    [Fact]
    public async Task Refresh_value_default_expiration()
    {
        var key = _fixture.Create<string>();
        var expected = _fixture.Create<string>();
        _memoryCache.TryGetValue(key, out Arg.Any<object?>())
        .Returns(x =>
        {
            x[1] = new TestCacheEntry<string> { Value = expected };
            return true;
        });
        await Sut.RefreshAsync<string>(key, CancellationToken.None);
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
    }

    [Fact]
    public async Task Refresh_value_TimeSpan()
    {
        var key = _fixture.Create<string>();
        var expected = _fixture.Create<string>();
        _memoryCache.TryGetValue(key, out Arg.Any<object?>())
        .Returns(x =>
        {
            x[1] = new TestCacheEntry<string> { Value = expected };
            return true;
        });
        var expiration = _fixture.Create<TimeSpan?>();
        await Sut.RefreshAsync<string>(key, expiration, CancellationToken.None);
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
    }

    [Fact]
    public async Task Refresh_value_DateTimeOffset()
    {
        var key = _fixture.Create<string>();
        var expected = _fixture.Create<string>();
        var expiration = DateTimeOffset.UtcNow.AddDays(1);
        _memoryCache.TryGetValue(key, out Arg.Any<object?>())
        .Returns(x =>
        {
            x[1] = new TestCacheEntry<string> { Value = expected };
            return true;
        });
        await Sut.RefreshAsync<string>(key, expiration, CancellationToken.None);
        _memoryCache.Received(1).CreateEntry(Arg.Any<object>());
    }

    [Fact]
    public async Task Refresh_notexisting_cache_entry_timespan()
    {
        var key = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan?>();
        await Sut.RefreshAsync<string>(key, expiration, CancellationToken.None);
        _memoryCache.DidNotReceive().CreateEntry(Arg.Any<object>());
    }

    [Fact]
    public async Task Refresh_notexisting_cache_entry_datetime()
    {
        var key = _fixture.Create<string>();
        var expiration = DateTimeOffset.UtcNow.AddDays(1);
        await Sut.RefreshAsync<string>(key, expiration, CancellationToken.None);
        _memoryCache.DidNotReceive().CreateEntry(Arg.Any<object>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Contains(bool expected)
    {
        var key = _fixture.Create<string>();
        var memoryCacheCalled = false;
        _memoryCache.TryGetValue(key, out Arg.Any<object?>())
            .Returns(x =>
            {
                memoryCacheCalled = true;
                return expected;
            });
        var actual = await Sut.ContainsAsync(key, CancellationToken.None);
        actual.Should().Be(expected);
        memoryCacheCalled.Should().BeTrue();
    }


    [Fact]
    public async Task Read_ExpireTime_For_Key()
    {
        var key = _fixture.Create<string>();
        var logger = _fixture.Freeze<ILogger<HybridCache>>();
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
        var sut = new MemCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemCache>>());
        await sut.SetAsync(key, 1, expiration, CancellationToken.None);
        var actual = await sut.ExpireTimeAsync(key);
        expiration.Should().Be(actual);
    }

    [Fact]
    public async Task Read_ExpireTimeToLive_For_Key()
    {
        var clock = new SystemClock();
        var key = _fixture.Create<string>();
        var logger = _fixture.Freeze<ILogger<HybridCache>>();
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
        var sut = new MemCache((opt) => memoryCache, _changeTokenFactory, _cachingTelemetryProvider, Options.Create(_cacheOptions), _fixture.Freeze<ILogger<MemCache>>());
        await sut.SetAsync(key, 1, expiration, CancellationToken.None);
        var actual = await sut.TimeToLiveAsync(key);
        expiration.Should().BeCloseTo(actual.GetValueOrDefault(), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task When_no_inner_cache_expire_time_use_max()
    {
        var key = _fixture.Create<string>();
        var expected = _fixture.Create<string>();
        Func<Task<string?>> generator = () =>
        {
            return Task.FromResult((string?)expected);
        };
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
        _fixture.Inject(Options.Create(_cacheOptions));
        _formatter = new CacheClearEventFormatterProxy();
        _fixture.Inject(_formatter);

        return Task.CompletedTask;
    }
}
