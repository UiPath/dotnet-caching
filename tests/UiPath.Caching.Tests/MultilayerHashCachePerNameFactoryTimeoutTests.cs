using UiPath.Caching.Locking;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

public class MultilayerHashCachePerNameFactoryTimeoutTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly ManualTimeProvider _clock = new();
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private global::Microsoft.Extensions.Caching.Memory.IMemoryCache _memoryCache = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private ITopicKeyStrategy _topicKeyStrategy = default!;
    private ITopicFactory _topicFactory = default!;
    private MultilayerCachePerNameLockTests.ITopicProviderWithConnectionState _topicProvider = default!;
    private ITopic<ICacheEvent> _topic = default!;
    private global::UiPath.Caching.IMemoryCacheFactory _memoryCacheFactory = default!;
    private InMemoryRedisCacheOptions _options = default!;
    private CacheKey _cacheKey = default!;
    private TopicKey _topicKey = default!;
    private MultilayerHashCache? _sut;

    private MultilayerHashCache Sut => _sut ??= RunTables.Track(_fixture.Create<MultilayerHashCache>());

    [Fact]
    public async Task GetOrAdd_with_null_FactoryTimeout_runs_generator_to_completion()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IDictionary<string, string?>> Generator(CancellationToken ct)
        {
            await release.Task.WaitAsync(ct);
            return new Dictionary<string, string?> { ["f"] = "v" };
        }

        var pending = Sut.GetOrAddAsync(_cacheKey, (Func<CancellationToken, Task<IDictionary<string, string?>>>)Generator, policy: null, token: token).AsTask();
        pending.IsCompleted.Should().BeFalse();
        release.SetResult();
        var result = await pending;

        result["f"].Should().Be("v");
    }

    [Fact]
    public async Task GetOrAdd_with_FactoryTimeout_throws_TimeoutException_when_generator_exceeds_budget()
    {
        using var caller = new CancellationTokenSource();
        var policy = new CachePolicy { FactoryTimeout = TimeSpan.FromMilliseconds(50) };
        async Task<IDictionary<string, string?>> Generator(CancellationToken ct)
        {
            await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(ct);
            return new Dictionary<string, string?> { ["f"] = "v" };
        }

        var pending = Sut.GetOrAddAsync(_cacheKey, Generator, policy, caller.Token).AsTask();
        _clock.Advance(policy.FactoryTimeout.Value);
        var act = async () => await pending;

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task GetOrAdd_with_FactoryTimeout_completes_when_generator_finishes_within_budget()
    {
        var policy = new CachePolicy { FactoryTimeout = TimeSpan.FromSeconds(5) };
        Task<IDictionary<string, string?>> Generator(CancellationToken _) =>
            Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["f"] = "v" });

        var result = await Sut.GetOrAddAsync(_cacheKey, (Func<CancellationToken, Task<IDictionary<string, string?>>>)Generator, policy, testContextAccessor.Current.CancellationToken);

        result["f"].Should().Be("v");
    }

    [Fact]
    public async Task GetOrAdd_FactoryTimeout_does_not_swallow_caller_cancellation()
    {
        var policy = new CachePolicy { FactoryTimeout = TimeSpan.FromSeconds(1) };
        using var cts = new CancellationTokenSource();
        async Task<IDictionary<string, string?>> Generator(CancellationToken ct)
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return new Dictionary<string, string?> { ["f"] = "v" };
        }

        var act = async () => await Sut.GetOrAddAsync(_cacheKey, Generator, policy, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _fixture.Inject<TimeProvider>(_clock);
        _cacheKey = _fixture.Create<string>();
        _topicKey = _fixture.Create<string>();
        _fixture.Freeze<IHashCache>();
        _memoryCache = _fixture.Freeze<global::Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        _fixture.Freeze<IDistributedLock>();
        _options = new InMemoryRedisCacheOptions
        {
            DefaultExpiration = TimeSpan.FromMinutes(10),
            EntryFactory = new TestCacheEntryFactory(),
            LocalSingleFlightEnabled = false,
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
        _memoryCacheFactory = _fixture.Freeze<global::UiPath.Caching.IMemoryCacheFactory>();
        _memoryCacheFactory.Get(Arg.Any<IMemoryCacheOptions>()).Returns(_memoryCache);
        _fixture.Freeze<ICacheEventFactory>();
        return ValueTask.CompletedTask;
    }
}
