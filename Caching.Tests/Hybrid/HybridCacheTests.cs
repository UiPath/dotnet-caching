using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using UiPath.Platform.Caching.Telemetry;
using UiPath.Platform.Caching.Tests.Broadcast;

namespace UiPath.Platform.Caching.Tests.Hybrid;

public class HybridCacheTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    private ICache _innerCache = default!;
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

    private HybridCache? _sut = null;

    private HybridCache Sut => _sut ??= _fixture.Create<HybridCache>();

    [Fact]
    public async Task Get_data_from_inner_cache()
    {
        var key = _fixture.Create<string>();
        var expected = _fixture.Create<string>();
        _innerCache.GetAsync<string>(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<string?>(expected));

        var actual = await Sut.GetAsync<string>(key, CancellationToken.None);
        _changeTokenFactory.Received(1).Create(_hybridCacheOptions.ChannelPrefix, Arg.Is<string>(x => x.EndsWith(key, StringComparison.InvariantCultureIgnoreCase)));
        _memoryCache.Received(1).CreateEntry(key);
        actual.Should().Be(expected);
    }

    [Fact]
    public async Task Get_data_from_memory_cache()
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
        await _innerCache.DidNotReceive().GetAsync<string>(key, Arg.Any<CancellationToken>());
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
        var key = _fixture.Create<string>();
        var expected = _fixture.Create<string>();
        var generatorExpected = _fixture.Create<string>();
        var generatorWasCalled = false;
        Func<Task<string?>> generator = () =>
        {
            generatorWasCalled = true;
            return Task.FromResult((string?)generatorExpected);
        };
        _innerCache.GetAsync<string>(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult((string?)expected));

        var actual = await Sut.GetOrAddAsync(key, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeFalse();
        actual.Should().Be(expected);
    }

    [Fact]
    public async Task GetOrAdd_data_from_inner_cache_default_expiration()
    {
        var key = _fixture.Create<string>();
        var expected = _fixture.Create<string>();
        var generatorExpected = _fixture.Create<string>();
        var generatorWasCalled = false;
        Func<Task<string?>> generator = () =>
        {
            generatorWasCalled = true;
            return Task.FromResult((string?)generatorExpected);
        };
        _innerCache.GetAsync<string>(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult((string?)expected));

        var actual = await Sut.GetOrAddAsync(key, generator, CancellationToken.None);
        generatorWasCalled.Should().BeFalse();
        actual.Should().Be(expected);
    }

    [Fact]
    public void Dispose_can_be_called()
    {
        Sut.Dispose();
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
        _innerCache.GetAsync<string>(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult(default(string?)));

        var actual = await Sut.GetOrAddAsync(key, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeTrue();
        _memoryCache.Received(1).CreateEntry(key);
        await _innerCache.Received(1).SetAsync(key, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
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
        _innerCache.GetAsync<string>(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult(default(string)));

        var actual = await Sut.GetOrAddAsync(key, generator, _fixture.Create<TimeSpan>(), CancellationToken.None);
        generatorWasCalled.Should().BeTrue();
        _memoryCache.Received(0).CreateEntry(key);
        await _innerCache.Received(0).SetAsync(key, Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Set_default_value()
    {
        var key = _fixture.Create<string>();
        _innerCache.GetAsync<string>(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult(default(string?)));

        var actual = await Sut.SetAsync(key, default(string), _fixture.Create<TimeSpan>(), CancellationToken.None);
        _memoryCache.Received(1).Remove(key);
        await _innerCache.Received(1).RemoveAsync<string>(key, Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_default_value_default_expiration()
    {
        var key = _fixture.Create<string>();
        _innerCache.GetAsync<string>(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult(default(string?)));

        var actual = await Sut.SetAsync(key, default(string), CancellationToken.None);
        _memoryCache.Received(1).Remove(key);
        await _innerCache.Received(1).RemoveAsync<string>(key, Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_value_value()
    {
        var key = _fixture.Create<string>();
        var value = _fixture.Create<string>();
        _innerCache.GetAsync<string>(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult(default(string?)));

        var actual = await Sut.SetAsync(key, value, _fixture.Create<TimeSpan>(), CancellationToken.None);
        _memoryCache.Received(0).Remove(key);
        await _innerCache.Received(0).RemoveAsync<string>(key, Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
        _memoryCache.Received(1).CreateEntry(key);
        await _innerCache.Received(1).SetAsync(key, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_value_inner_cache_throw_exception()
    {
        var key = _fixture.Create<string>();
        var value = _fixture.Create<string>();
        _innerCache.SetAsync(key, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync<Exception>();

        var actual = await Sut.SetAsync(key, value, _fixture.Create<TimeSpan>(), CancellationToken.None);
        actual.Should().BeFalse();

        actual = await Sut.SetAsync(key, value, _fixture.Create<DateTimeOffset>(), CancellationToken.None);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_default_value()
    {
        var key = _fixture.Create<string>();
        _innerCache.GetAsync<string>(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult(default(string?)));
        _channelPublisher.PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>())
            .Throws<Exception>();
        var actual = await Sut.RemoveAsync<string>(key, CancellationToken.None);
        _memoryCache.Received(1).Remove(key);
        await _innerCache.Received(1).RemoveAsync<string>(key, Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_default_value_error()
    {
        var key = _fixture.Create<string>();
        _innerCache.GetAsync<string>(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult(default(string?)));
        var actual = await Sut.RemoveAsync<string>(key, CancellationToken.None);
        _memoryCache.Received(1).Remove(key);
        await _innerCache.Received(1).RemoveAsync<string>(key, Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
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
        _innerCache.GetAsync<string>(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<string?>(expected));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_hybridCacheOptions.ChannelPrefix, Arg.Is<string>(x => x.EndsWith(key, StringComparison.InvariantCultureIgnoreCase)), Arg.Any<Uri?>())
            .Returns(c => token);
        var sut = new HybridCache(_innerCache, (opt) => memoryCache, _changeTokenFactory, _channelPublisher, _channelResolver, _cacheEventFactory, _cachingTelemetryProvider, Options.Create(_hybridCacheOptions), _fixture.Freeze<ILogger<HybridCache>>());
        var actual = await sut.GetAsync<string>(key, CancellationToken.None);
        await sut.RemoveAsync<string>(key);
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

        var key = _fixture.Create<string>();
        var expected = _fixture.Create<string>();

        _innerCache.GetAsync<string>(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<string?>(expected));
        _innerCache.ExpireTimeAsync(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult((DateTimeOffset?)clock.UtcNow.AddDays(1)));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_hybridCacheOptions.ChannelPrefix, key, Arg.Any<Uri?>())
            .ReturnsForAnyArgs(c => token);
        var sut = new HybridCache(_innerCache, (opt) => memoryCache, _changeTokenFactory, _channelPublisher, _channelResolver, _cacheEventFactory, _cachingTelemetryProvider, Options.Create(_hybridCacheOptions), _fixture.Freeze<ILogger<HybridCache>>());
        var actual = await sut.GetAsync<string>(key, CancellationToken.None);
        token.HasChanged = true;
        token.InvokeCallbacks();
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
        _innerCache.GetAsync<string>(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<string?>(expected));
        _innerCache.ExpireTimeAsync(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult((DateTimeOffset?)now.AddDays(1)));
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = false,
            HasChanged = false
        };
        _changeTokenFactory.Create(_hybridCacheOptions.ChannelPrefix, key, Arg.Any<Uri?>())
            .ReturnsForAnyArgs(c => token);
        var sut = new HybridCache(_innerCache, (d) => memoryCache, _changeTokenFactory, _channelPublisher, _channelResolver, _cacheEventFactory, _cachingTelemetryProvider, Options.Create(new HybridCacheOptions
        {
            Clock = clock,
        }), _fixture.Freeze<ILogger<HybridCache>>());
        var actual = await sut.GetAsync<string>(key, CancellationToken.None);
        token.Callbacks.Should().BeEmpty();
        memoryCache.TryGetValue(key, out _).Should().BeTrue();
        token.HasChanged = true;
        memoryCache.TryGetValue(key, out _).Should().BeFalse();
    }
    [Fact]
    public async Task Refresh_value_default_expiration()
    {
        var key = _fixture.Create<string>();
        await Sut.RefreshAsync<string>(key, CancellationToken.None);
        _memoryCache.Received(1).Remove(key);
        await _innerCache.Received(1).RefreshAsync<string>(key, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_value_TimeSpan()
    {
        var key = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan?>();
        await Sut.RefreshAsync<string>(key, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(key);
        await _innerCache.Received(1).RefreshAsync<string>(key, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_value_DateTimeOffset()
    {
        var key = _fixture.Create<string>();
        var expiration = DateTimeOffset.UtcNow.AddDays(1);
        await Sut.RefreshAsync<string>(key, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(key);
        await _innerCache.Received(1).RefreshAsync<string>(key, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _channelPublisher.Received(1).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_inner_cache_exception_timespan()
    {
        var key = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan?>();
        _innerCache.RefreshAsync<string>(key, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync<Exception>();
        await Sut.RefreshAsync<string>(key, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(key);
        await _innerCache.Received(1).RefreshAsync<string>(key, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _channelPublisher.Received(0).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_inner_cache_exception_DateTimeOffset()
    {
        var key = _fixture.Create<string>();
        var expiration = DateTimeOffset.UtcNow.AddDays(1);
        _innerCache.RefreshAsync<string>(key, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync<Exception>();
        await Sut.RefreshAsync<string>(key, expiration, CancellationToken.None);
        _memoryCache.Received(1).Remove(key);
        await _innerCache.Received(1).RefreshAsync<string>(key, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _channelPublisher.Received(0).PublishAsync(_hybridCacheOptions.ChannelPrefix, Arg.Any<IClearCacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Contains_in_inner_cache()
    {
        var key = _fixture.Create<string>();
        var expected = _fixture.Create<bool>();
        var memoryCacheCalled = false;
        _memoryCache.TryGetValue(key, out Arg.Any<object?>())
            .Returns(x =>
            {
                memoryCacheCalled = true;
                return false;
            });
        _innerCache.ContainsAsync(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult(expected));
        var actual = await Sut.ContainsAsync(key, CancellationToken.None);
        actual.Should().Be(expected);
        memoryCacheCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_in_memory_cache()
    {
        var key = _fixture.Create<string>();
        var expected = _fixture.Create<bool>();
        var memoryCacheCalled = false;
        _memoryCache.TryGetValue(key, out Arg.Any<object?>())
            .Returns(x =>
            {
                memoryCacheCalled = true;
                return true;
            });
        var actual = await Sut.ContainsAsync(key, CancellationToken.None);
        await _innerCache.DidNotReceive().ContainsAsync(key, CancellationToken.None);
        actual.Should().Be(expected);
        memoryCacheCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_in_inner_cache_exception()
    {
        var key = _fixture.Create<string>();
        var expected = _fixture.Create<bool>();
        var memoryCacheCalled = false;
        _memoryCache.TryGetValue(key, out Arg.Any<object?>())
            .Returns(x =>
            {
                memoryCacheCalled = true;
                return false;
            });
        _innerCache.ContainsAsync(key, CancellationToken.None)
            .ThrowsAsync<Exception>();
        var actual = await Sut.ContainsAsync(key, CancellationToken.None);
        actual.Should().Be(false);
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
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_hybridCacheOptions.ChannelPrefix, Arg.Any<string>(), Arg.Any<Uri?>())
            .Returns(c => token);

        var expiration = _clock.UtcNow.AddYears(1);
        var sut = new HybridCache(_innerCache, (opt) => memoryCache, _changeTokenFactory, _channelPublisher, _channelResolver, _cacheEventFactory, _cachingTelemetryProvider, Options.Create(_hybridCacheOptions), _fixture.Freeze<ILogger<HybridCache>>());
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
        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false
        };
        _changeTokenFactory.Create(_hybridCacheOptions.ChannelPrefix, Arg.Any<string>(), Arg.Any<Uri?>())
            .Returns(c => token);

        var expiration = TimeSpan.FromDays(1);
        var sut = new HybridCache(_innerCache, (opt) => memoryCache, _changeTokenFactory, _channelPublisher, _channelResolver, _cacheEventFactory, _cachingTelemetryProvider, Options.Create(_hybridCacheOptions), _fixture.Freeze<ILogger<HybridCache>>());
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

        _innerCache.GetAsync<string>(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult<string?>(expected));
        _innerCache.ExpireTimeAsync(key, CancellationToken.None)
            .ReturnsForAnyArgs(Task.FromResult(default(DateTimeOffset?)));
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
        _innerCache = _fixture.Freeze<ICache>();
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
