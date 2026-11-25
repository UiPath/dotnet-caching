using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Policies;

namespace UiPath.Caching.Tests.Redis;

/// <summary>Two nodes on one Redis with broadcast, the writes of one slowed so the order of its value and event shows.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class BackgroundWriteIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private readonly RedisTestSignals _signals = new();
    private readonly string _app = $"bw{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_peer_woken_by_a_set_never_keeps_the_value_the_set_replaced()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writes = new GatedWrites { Completed = _signals.Pulse };
        var (_, writer) = Node("a", writes);
        var (_, peer) = Node("b");
        var key = Unique();
        writes.Open();
        (await writer.SetAsync(key, "old", Ct)).Should().BeTrue();
        (await peer.GetAsync<string>(key, Ct)).Should().Be("old");

        writes.Hold();
        var set = writer.SetAsync(key, "new", Ct).AsTask();
        await writes.Entered.Task.WaitAsync(Ct);
        set.IsCompleted.Should().BeFalse();
        _ = await peer.GetAsync<string>(key, Ct);
        writes.Open();

        (await set).Should().BeTrue();
        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "new")).Should().BeTrue("the peer drops its copy on the event and the store already holds the new value");
    }

    [Fact]
    public async Task A_background_set_returns_before_its_write_and_the_value_reaches_redis_and_the_peer_after()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writes = new GatedWrites { Completed = _signals.Pulse };
        var (_, writer) = Node("a", writes, background: true);
        var (_, peer) = Node("b");
        var key = Unique();

        (await writer.SetAsync(key, "v", Ct)).Should().BeTrue();

        await writes.Entered.Task.WaitAsync(Ct);
        writes.IsHeld.Should().BeTrue("the caller returned while the write was held");
        (await writer.GetAsync<string>(key, Ct)).Should().Be("v", "the value is local at once");
        (await peer.GetAsync<string>(key, Ct)).Should().BeNull("the write has not reached the store yet");
        writes.Open();
        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "v")).Should().BeTrue();
    }

    [Fact]
    public async Task A_background_set_replacing_a_value_invalidates_the_peer_only_after_the_write()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writes = new GatedWrites { Completed = _signals.Pulse };
        var (_, writer) = Node("a", writes, background: true);
        var (peerNode, peer) = Node("b");
        var key = Unique();
        writes.Open();
        (await writer.SetAsync(key, "old", Ct)).Should().BeTrue();
        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "old")).Should().BeTrue();
        await StreamsReady.AwaitSubscribedAsync(peerNode);

        writes.Hold();
        (await writer.SetAsync(key, "new", Ct)).Should().BeTrue();
        await writes.Entered.Task.WaitAsync(Ct);
        (await peer.GetAsync<string>(key, Ct)).Should().Be("old");
        writes.Open();
        await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "new");

        (await peer.GetAsync<string>(key, Ct)).Should().Be("new");
    }

    [Fact]
    public async Task Background_sets_of_one_key_land_in_redis_in_the_order_they_were_issued_when_the_first_write_is_slow()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writes = new GatedWrites(firstOnly: true) { Completed = _signals.Pulse };
        var (_, writer) = Node("a", writes, background: true);
        var (_, peer) = Node("b");
        var key = Unique();

        (await writer.SetAsync(key, "v1", Ct)).Should().BeTrue();
        (await writer.SetAsync(key, "v2", Ct)).Should().BeTrue();
        writes.Open();

        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "v2")).Should().BeTrue();
        await writes.FirstCompleted.Task.WaitAsync(Ct);
        (await peer.GetAsync<string>(key, Ct)).Should().Be("v2", "the slow first write must not land after the second");
    }

    [Fact]
    public async Task Disposing_the_cache_lets_the_background_write_finish()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writes = new GatedWrites { Completed = _signals.Pulse };
        var (node, writer) = Node("a", writes, background: true);
        var (_, peer) = Node("b");
        var key = Unique();
        (await writer.SetAsync(key, "v", Ct)).Should().BeTrue();

        var disposing = Task.Run(async () => await node.DisposeAsync(), Ct);
        await writes.Entered.Task.WaitAsync(Ct);
        writes.Open();
        await disposing;

        (await peer.GetAsync<string>(key, Ct)).Should().Be("v");
    }

    [Fact]
    public async Task A_value_changed_after_a_background_set_returns_is_stored_as_it_was()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writes = new GatedWrites { Completed = _signals.Pulse };
        var (_, writer) = Node("a", writes, background: true);
        var (_, peer) = Node("b");
        var key = Unique();
        var value = new Box { Text = "before" };

        (await writer.SetAsync(key, value, Ct)).Should().BeTrue();
        value.Text = "after";
        writes.Open();

        (await EventuallyAsync(async () => await peer.GetAsync<Box>(key, Ct) is not null)).Should().BeTrue();
        (await peer.GetAsync<Box>(key, Ct))!.Text.Should().Be("before");
    }

    [Fact]
    public async Task A_background_fill_returns_before_its_write_and_the_peer_finds_it_after()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writes = new GatedWrites { Completed = _signals.Pulse };
        var (_, writer) = Node("a", writes, background: true);
        var (_, peer) = Node("b");
        var key = Unique();

        (await writer.GetOrAddAsync<string>(key, static _ => Task.FromResult<string?>("gen"), TimeSpan.FromMinutes(5), Ct)).Should().Be("gen");

        await writes.Entered.Task.WaitAsync(Ct);
        writes.IsHeld.Should().BeTrue("the caller returned while the write was held");
        writes.Open();
        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "gen")).Should().BeTrue();
    }

    [Fact]
    public async Task A_peer_woken_by_a_hash_set_never_keeps_the_hash_the_set_replaced()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writes = new GatedWrites { Completed = _signals.Pulse };
        var (writerNode, _) = Node("a", writes);
        var (peerNode, _) = Node("b");
        var writer = Hashes(writerNode);
        var peer = Hashes(peerNode);
        var key = Unique();
        writes.Open();
        (await writer.SetAsync(key, Fields("old"), Ct)).Should().BeTrue();
        (await peer.GetAsync<string>(key, Ct))!["f"].Should().Be("old");

        writes.Hold();
        var set = writer.SetAsync(key, Fields("new"), Ct).AsTask();
        await writes.Entered.Task.WaitAsync(Ct);
        set.IsCompleted.Should().BeFalse();
        _ = await peer.GetAsync<string>(key, Ct);
        writes.Open();

        (await set).Should().BeTrue();
        (await EventuallyAsync(async () => Field(await peer.GetAsync<string>(key, Ct)) == "new")).Should().BeTrue();
    }

    [Fact]
    public async Task A_background_hash_set_returns_before_its_write_and_the_hash_reaches_redis_after()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writes = new GatedWrites { Completed = _signals.Pulse };
        var (writerNode, _) = Node("a", writes, background: true);
        var (peerNode, _) = Node("b");
        var writer = Hashes(writerNode);
        var peer = Hashes(peerNode);
        var key = Unique();

        (await writer.SetAsync(key, Fields("v"), Ct)).Should().BeTrue();

        await writes.Entered.Task.WaitAsync(Ct);
        writes.IsHeld.Should().BeTrue("the caller returned while the write was held");
        (await writer.GetAsync<string>(key, Ct))!["f"].Should().Be("v");
        writes.Open();
        (await EventuallyAsync(async () => Field(await peer.GetAsync<string>(key, Ct)) == "v")).Should().BeTrue();
    }

    [Fact]
    public async Task Background_hash_sets_of_one_key_land_in_redis_in_the_order_they_were_issued_when_the_first_write_is_slow()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writes = new GatedWrites(firstOnly: true) { Completed = _signals.Pulse };
        var (writerNode, _) = Node("a", writes, background: true);
        var (peerNode, _) = Node("b");
        var writer = Hashes(writerNode);
        var peer = Hashes(peerNode);
        var key = Unique();

        await writer.SetAsync(key, Fields("v1"), Ct);
        await writer.SetAsync(key, Fields("v2"), Ct);
        writes.Open();

        (await EventuallyAsync(async () => Field(await peer.GetAsync<string>(key, Ct)) == "v2")).Should().BeTrue();
        await writes.FirstCompleted.Task.WaitAsync(Ct);
        (await peer.GetAsync<string>(key, Ct))!["f"].Should().Be("v2");
    }

    [Fact]
    public async Task A_background_hash_fill_returns_before_its_write_and_disposing_the_cache_lets_it_finish()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writes = new GatedWrites { Completed = _signals.Pulse };
        var (writerNode, _) = Node("a", writes, background: true);
        var (peerNode, _) = Node("b");
        var writer = Hashes(writerNode);
        var peer = Hashes(peerNode);
        var key = Unique();

        (await writer.GetOrAddAsync<string>(key, static _ => Task.FromResult<IDictionary<string, string?>>(Fields("gen")), TimeSpan.FromMinutes(5), Ct))["f"].Should().Be("gen");
        await writes.Entered.Task.WaitAsync(Ct);
        writes.IsHeld.Should().BeTrue("the caller returned while the write was held");

        var disposing = Task.Run(async () => await writerNode.DisposeAsync(), Ct);
        writes.Open();
        await disposing;

        (await peer.GetAsync<string>(key, Ct))!["f"].Should().Be("gen");
    }

    [Fact]
    public async Task The_distributed_cache_adapter_waits_for_its_write_even_when_the_default_policy_writes_in_the_background()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var services = new ServiceCollection();
        var writes = new GatedWrites { Completed = _signals.Pulse };
        services.AddSingleton<IResiliencePipelineProvider>(new WritePipelineProvider(writes));
        var provider = services
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis();
                    b.AddInMemoryRedis(_ => { });
                    b.AddDistributedCache(KnownCacheProviderNames.InMemoryRedis);
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.DefaultCachePolicy = new CachePolicy { BackgroundDistributedWrite = true };
                })
            .ObserveRedisSignals(_signals)
            .BuildServiceProvider();
        _nodes.Add(provider);
        StreamsReady.Await(provider);
        var cache = provider.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();

        var set = cache.SetAsync(Unique(), [1, 2, 3], new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, Ct);
        await writes.Entered.Task.WaitAsync(Ct);
        set.IsCompleted.Should().BeFalse("the adapter must keep the payload until its write completes");
        writes.Open();
        await set;

    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static Dictionary<string, string?> Fields(string value) => new() { ["f"] = value };

    private static IHashCache Hashes(ServiceProvider node) => node.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.InMemoryRedis);

    private static string? Field(IDictionary<string, string?>? hash) => hash is not null && hash.TryGetValue("f", out var value) ? value : null;

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    private Task<bool> EventuallyAsync(Func<Task<bool>> condition) => _signals.UntilAsync(condition, Ct);

    private (ServiceProvider Provider, ICache Cache) Node(string source, IResiliencePipeline? writes = null, bool background = false)
    {
        var services = new ServiceCollection();
        if (writes is not null)
        {
            services.AddSingleton<IResiliencePipelineProvider>(new WritePipelineProvider(writes));
        }

        var provider = services
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis();
                    b.AddBroadcast();
                    b.AddInMemoryRedis(o => o.LocalMaxExpiration = TimeSpan.FromMinutes(1));
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.SourceUri = new Uri($"urn:{source}-{_app}");
                    o.DefaultCachePolicy = new CachePolicy { BackgroundDistributedWrite = background };
                })
            .ObserveRedisSignals(_signals)
            .BuildServiceProvider();
        _nodes.Add(provider);
        StreamsReady.Await(provider);
        return (provider, provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemoryRedis));
    }

    private sealed class Box
    {
        public string? Text { get; set; }
    }

    private sealed class WritePipelineProvider(IResiliencePipeline writes) : IResiliencePipelineProvider
    {
        public IResiliencePipeline Get(string? name) => name == ResiliencePipelineNames.Write ? writes : EmptyResiliencePipeline.Instance;
    }

    private sealed class GatedWrites(bool firstOnly = false) : IResiliencePipeline
    {
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action? Completed { get; init; }

        public bool IsHeld => !_release.Task.IsCompleted;

        public void Open() => _release.TrySetResult();

        public void Hold()
        {
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
        {
            var first = Interlocked.Increment(ref _calls) == 1;
            if (!firstOnly || first)
            {
                var release = _release.Task;
                Entered.TrySetResult();
                await release.ConfigureAwait(false);
            }

            var result = await callback(cancellationToken).ConfigureAwait(false);
            Completed?.Invoke();
            if (first)
            {
                FirstCompleted.TrySetResult();
            }

            return result;
        }
    }
}
