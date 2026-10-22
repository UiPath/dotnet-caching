using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Policies;

namespace UiPath.Caching.Tests.Redis;

/// <summary>Two nodes on one Redis with broadcast, the writes of one slowed so the order of its value and event shows.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class BackgroundWriteIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(150);
    private readonly string _app = $"bw{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_peer_woken_by_a_set_never_keeps_the_value_the_set_replaced()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (_, writer) = Node("a", new DelayedWrites(Slow));
        var (_, peer) = Node("b");
        var key = Unique();
        (await writer.SetAsync(key, "old", Ct)).Should().BeTrue();
        (await peer.GetAsync<string>(key, Ct)).Should().Be("old");

        var set = writer.SetAsync(key, "new", Ct).AsTask();
        // Reading as the invalidation lands: a value read from the store before the write reaches it would stay local for the whole lifetime.
        while (!set.IsCompleted)
        {
            _ = await peer.GetAsync<string>(key, Ct);
            await Task.Delay(2, Ct);
        }

        (await set).Should().BeTrue();
        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "new")).Should().BeTrue("the peer drops its copy on the event and the store already holds the new value");
    }

    [Fact]
    public async Task A_background_set_returns_before_its_write_and_the_value_reaches_redis_and_the_peer_after()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (_, writer) = Node("a", new DelayedWrites(TimeSpan.FromMilliseconds(600)), background: true);
        var (_, peer) = Node("b");
        var key = Unique();

        var started = DateTime.UtcNow;
        (await writer.SetAsync(key, "v", Ct)).Should().BeTrue();

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromMilliseconds(400), "the write itself takes 600 ms");
        (await writer.GetAsync<string>(key, Ct)).Should().Be("v", "the value is local at once");
        (await peer.GetAsync<string>(key, Ct)).Should().BeNull("the write has not reached the store yet");
        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "v")).Should().BeTrue();
    }

    [Fact]
    public async Task A_background_set_replacing_a_value_invalidates_the_peer_only_after_the_write()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (_, writer) = Node("a", new DelayedWrites(Slow), background: true);
        var (_, peer) = Node("b");
        var key = Unique();
        (await writer.SetAsync(key, "old", Ct)).Should().BeTrue();
        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "old")).Should().BeTrue();

        (await writer.SetAsync(key, "new", Ct)).Should().BeTrue();
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until)
        {
            _ = await peer.GetAsync<string>(key, Ct);
            await Task.Delay(2, Ct);
        }

        (await peer.GetAsync<string>(key, Ct)).Should().Be("new");
    }

    [Fact]
    public async Task Background_sets_of_one_key_land_in_redis_in_the_order_they_were_issued_when_the_first_write_is_slow()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (_, writer) = Node("a", new SlowFirstWrite(TimeSpan.FromMilliseconds(400)), background: true);
        var (_, peer) = Node("b");
        var key = Unique();

        (await writer.SetAsync(key, "v1", Ct)).Should().BeTrue();
        (await writer.SetAsync(key, "v2", Ct)).Should().BeTrue();

        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "v2")).Should().BeTrue();
        await Task.Delay(700, Ct);
        (await peer.GetAsync<string>(key, Ct)).Should().Be("v2", "the slow first write must not land after the second");
    }

    [Fact]
    public async Task Disposing_the_cache_lets_the_background_write_finish()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (node, writer) = Node("a", new DelayedWrites(TimeSpan.FromMilliseconds(500)), background: true);
        var (_, peer) = Node("b");
        var key = Unique();
        (await writer.SetAsync(key, "v", Ct)).Should().BeTrue();

        node.Dispose();

        (await peer.GetAsync<string>(key, Ct)).Should().Be("v");
    }

    [Fact]
    public async Task A_value_changed_after_a_background_set_returns_is_stored_as_it_was()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (_, writer) = Node("a", new DelayedWrites(TimeSpan.FromMilliseconds(300)), background: true);
        var (_, peer) = Node("b");
        var key = Unique();
        var value = new Box { Text = "before" };

        (await writer.SetAsync(key, value, Ct)).Should().BeTrue();
        value.Text = "after";

        (await EventuallyAsync(async () => await peer.GetAsync<Box>(key, Ct) is not null)).Should().BeTrue();
        (await peer.GetAsync<Box>(key, Ct))!.Text.Should().Be("before");
    }

    [Fact]
    public async Task A_background_fill_returns_before_its_write_and_the_peer_finds_it_after()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (_, writer) = Node("a", new DelayedWrites(TimeSpan.FromMilliseconds(600)), background: true);
        var (_, peer) = Node("b");
        var key = Unique();

        var started = DateTime.UtcNow;
        (await writer.GetOrAddAsync<string>(key, static _ => Task.FromResult<string?>("gen"), TimeSpan.FromMinutes(5), Ct)).Should().Be("gen");

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromMilliseconds(400));
        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "gen")).Should().BeTrue();
    }

    [Fact]
    public async Task A_peer_woken_by_a_hash_set_never_keeps_the_hash_the_set_replaced()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (writerNode, _) = Node("a", new DelayedWrites(Slow));
        var (peerNode, _) = Node("b");
        var writer = Hashes(writerNode);
        var peer = Hashes(peerNode);
        var key = Unique();
        (await writer.SetAsync(key, Fields("old"), Ct)).Should().BeTrue();
        (await peer.GetAsync<string>(key, Ct))!["f"].Should().Be("old");

        var set = writer.SetAsync(key, Fields("new"), Ct).AsTask();
        while (!set.IsCompleted)
        {
            _ = await peer.GetAsync<string>(key, Ct);
            await Task.Delay(2, Ct);
        }

        (await set).Should().BeTrue();
        (await EventuallyAsync(async () => Field(await peer.GetAsync<string>(key, Ct)) == "new")).Should().BeTrue();
    }

    [Fact]
    public async Task A_background_hash_set_returns_before_its_write_and_the_hash_reaches_redis_after()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (writerNode, _) = Node("a", new DelayedWrites(TimeSpan.FromMilliseconds(600)), background: true);
        var (peerNode, _) = Node("b");
        var writer = Hashes(writerNode);
        var peer = Hashes(peerNode);
        var key = Unique();

        var started = DateTime.UtcNow;
        (await writer.SetAsync(key, Fields("v"), Ct)).Should().BeTrue();

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromMilliseconds(400));
        (await writer.GetAsync<string>(key, Ct))!["f"].Should().Be("v");
        (await EventuallyAsync(async () => Field(await peer.GetAsync<string>(key, Ct)) == "v")).Should().BeTrue();
    }

    [Fact]
    public async Task Background_hash_sets_of_one_key_land_in_redis_in_the_order_they_were_issued_when_the_first_write_is_slow()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (writerNode, _) = Node("a", new SlowFirstWrite(TimeSpan.FromMilliseconds(400)), background: true);
        var (peerNode, _) = Node("b");
        var writer = Hashes(writerNode);
        var peer = Hashes(peerNode);
        var key = Unique();

        await writer.SetAsync(key, Fields("v1"), Ct);
        await writer.SetAsync(key, Fields("v2"), Ct);

        (await EventuallyAsync(async () => Field(await peer.GetAsync<string>(key, Ct)) == "v2")).Should().BeTrue();
        await Task.Delay(700, Ct);
        (await peer.GetAsync<string>(key, Ct))!["f"].Should().Be("v2");
    }

    [Fact]
    public async Task A_background_hash_fill_returns_before_its_write_and_disposing_the_cache_lets_it_finish()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (writerNode, _) = Node("a", new DelayedWrites(TimeSpan.FromMilliseconds(600)), background: true);
        var (peerNode, _) = Node("b");
        var writer = Hashes(writerNode);
        var peer = Hashes(peerNode);
        var key = Unique();

        var started = DateTime.UtcNow;
        (await writer.GetOrAddAsync<string>(key, static _ => Task.FromResult<IDictionary<string, string?>>(Fields("gen")), TimeSpan.FromMinutes(5), Ct))["f"].Should().Be("gen");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromMilliseconds(400));

        writerNode.Dispose();

        (await peer.GetAsync<string>(key, Ct))!["f"].Should().Be("gen");
    }

    [Fact]
    public async Task The_distributed_cache_adapter_waits_for_its_write_even_when_the_default_policy_writes_in_the_background()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var services = new ServiceCollection();
        services.AddSingleton<IResiliencePipelineProvider>(new WritePipelineProvider(new DelayedWrites(TimeSpan.FromMilliseconds(500))));
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
            .BuildServiceProvider();
        _nodes.Add(provider);
        var cache = provider.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();

        var started = DateTime.UtcNow;
        await cache.SetAsync(Unique(), [1, 2, 3], new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, Ct);

        (DateTime.UtcNow - started).Should().BeGreaterThan(TimeSpan.FromMilliseconds(450), "the adapter's payload may be memory the caller reclaims on return");
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

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(20, Ct);
        }

        return false;
    }

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
            .BuildServiceProvider();
        _nodes.Add(provider);
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

    private sealed class DelayedWrites(TimeSpan delay) : IResiliencePipeline
    {
        public async ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
            return await callback(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class SlowFirstWrite(TimeSpan delay) : IResiliencePipeline
    {
        private int _calls;

        public async ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
            }

            return await callback(cancellationToken).ConfigureAwait(false);
        }
    }
}
