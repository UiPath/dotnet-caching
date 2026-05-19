using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using UiPath.Platform.Caching.Config;
using UiPath.Platform.Caching.Locking;

namespace UiPath.Platform.Caching.Tests;

public class MultilayerCachePerNameFactoryTimeoutTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ICache _innerCache = default!;
    private global::Microsoft.Extensions.Caching.Memory.IMemoryCache _memoryCache = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private ITopicKeyStrategy _topicKeyStrategy = default!;
    private ITopicFactory _topicFactory = default!;
    private MultilayerCachePerNameLockTests.ITopicProviderWithConnectionState _topicProvider = default!;
    private ITopic<ICacheEvent> _topic = default!;
    private global::UiPath.Platform.Caching.IMemoryCacheFactory _memoryCacheFactory = default!;
    private ICacheEventFactory _cacheEventFactory = default!;
    private ILocalLock _localLock = default!;
    private IDistributedLock _distributedLock = default!;
    private InMemoryRedisCacheOptions _options = default!;
    private CacheKey _cacheKey = default!;
    private TopicKey _topicKey = default!;
    private MultilayerCache? _sut;

    private MultilayerCache Sut => _sut ??= _fixture.Create<MultilayerCache>();

    [Fact]
    public async Task GetOrAdd_with_null_FactoryTimeout_runs_generator_to_completion()
    {
        var token = testContextAccessor.Current.CancellationToken;
        Func<CancellationToken, Task<string?>> generator = async ct =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
            return "v";
        };

        var result = await Sut.GetOrAddAsync(_cacheKey, generator, policy: null, token: token);

        result.Should().Be("v");
    }

    [Fact]
    public async Task GetOrAdd_with_FactoryTimeout_throws_TimeoutException_when_generator_exceeds_budget()
    {
        var policy = new CachePolicy { FactoryTimeout = TimeSpan.FromMilliseconds(50) };
        Func<CancellationToken, Task<string?>> generator = async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return "v";
        };

        var act = async () => await Sut.GetOrAddAsync(_cacheKey, generator, policy, testContextAccessor.Current.CancellationToken);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task GetOrAdd_with_FactoryTimeout_completes_when_generator_finishes_within_budget()
    {
        var policy = new CachePolicy { FactoryTimeout = TimeSpan.FromSeconds(5) };
        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("v");

        var result = await Sut.GetOrAddAsync(_cacheKey, generator, policy, testContextAccessor.Current.CancellationToken);

        result.Should().Be("v");
    }

    [Fact]
    public async Task GetOrAdd_FactoryTimeout_does_not_swallow_caller_cancellation()
    {
        var policy = new CachePolicy { FactoryTimeout = TimeSpan.FromSeconds(10) };
        using var cts = new CancellationTokenSource();
        Func<CancellationToken, Task<string?>> generator = async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return "v";
        };

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = async () => await Sut.GetOrAddAsync(_cacheKey, generator, policy, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetOrAdd_with_FactoryTimeout_larger_than_int_max_ms_clamps_and_runs_generator()
    {
        // CancellationTokenSource.CancelAfter throws for delays > Int32.MaxValue ms (~24 days).
        // A misconfigured FactoryTimeout (e.g. TimeSpan.MaxValue) must clamp internally rather
        // than surface as ArgumentOutOfRangeException at first cache miss.
        var policy = new CachePolicy { FactoryTimeout = TimeSpan.FromDays(365 * 100) };
        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("v");

        var result = await Sut.GetOrAddAsync(_cacheKey, generator, policy, testContextAccessor.Current.CancellationToken);

        result.Should().Be("v");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask InitializeAsync()
    {
        _cacheKey = _fixture.Create<string>();
        _topicKey = _fixture.Create<string>();
        _innerCache = _fixture.Freeze<ICache>();
        _memoryCache = _fixture.Freeze<global::Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        _localLock = _fixture.Freeze<ILocalLock>();
        _distributedLock = _fixture.Freeze<IDistributedLock>();
        _options = new InMemoryRedisCacheOptions
        {
            DefaultExpiration = TimeSpan.FromMinutes(10),
            EntryFactory = new TestCacheEntryFactory(),
            LocalLockEnabled = false,
            DistributedLockEnabled = false,
        };
        _fixture.Inject<IMultilayerCacheOptions>(_options);
        _fixture.Inject<IMemoryCacheOptions>(_options);
        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        _topicKeyStrategy = _fixture.Create<ITopicKeyStrategy>();
        _cacheKeyStrategy.GetCacheKey<string>(_cacheKey).Returns(_cacheKey);
        _topicKeyStrategy.GetTopicKey<string>().Returns(_topicKey);
        _topicFactory = _fixture.Freeze<ITopicFactory>();
        _topicProvider = _fixture.Freeze<MultilayerCachePerNameLockTests.ITopicProviderWithConnectionState>();
        _topic = _fixture.Freeze<ITopic<ICacheEvent>>();
        _topicFactory.Get(Arg.Any<string>()).Returns(_topicProvider);
        _topicProvider.Create(_topicKey).Returns(_topic);
        _memoryCacheFactory = _fixture.Freeze<global::UiPath.Platform.Caching.IMemoryCacheFactory>();
        _memoryCacheFactory.Get(Arg.Any<IMemoryCacheOptions>()).Returns(_memoryCache);
        _cacheEventFactory = _fixture.Freeze<ICacheEventFactory>();
        return ValueTask.CompletedTask;
    }
}
