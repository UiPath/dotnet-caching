using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Redis;

/// <summary>Miss fills on a real Redis: the write is NX, a loser takes the stored value, and a hash too large for one command is renamed under the key only if the key is absent.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class SafeFillRedisIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly string _app = $"sf{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];
    private ConnectionMultiplexer? _raw;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Raw => (_raw ??= ConnectionMultiplexer.Connect(fixture.ConnectionString)).GetDatabase();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nodes_that_miss_together_return_and_keep_the_value_stored_first(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var a = Node("a", scripts);
        var b = Node("b", scripts);
        var key = $"it-{Guid.NewGuid():N}";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Generate(CancellationToken _)
        {
            started.SetResult();
            await release.Task;
            return "from-a";
        }

        var slow = a.GetOrAddAsync<string>(key, Generate, Ttl, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        (await b.GetOrAddAsync<string>(key, _ => Task.FromResult<string?>("from-b"), Ttl, Ct)).Should().Be("from-b");
        release.SetResult();

        (await slow).Should().Be("from-b");
        (await a.GetAsync<string>(key, Ct)).Should().Be("from-b");
        (await Raw.StringGetAsync(Find(key))).ToString().Should().Contain("from-b");
        (await Raw.KeyTimeToLiveAsync(Find(key)))!.Value.Should().BeCloseTo(Ttl, TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_batch_fill_writes_the_absent_keys_and_returns_the_value_stored_first_for_a_held_one(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var a = Node("a", scripts);
        var b = Node("b", scripts);
        var (x, y, z) = ($"it-{Guid.NewGuid():N}", $"it-{Guid.NewGuid():N}", $"it-{Guid.NewGuid():N}");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<KeyValuePair<int, string?>[]> Generate(int[] states, CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            return [.. states.Select(s => new KeyValuePair<int, string?>(s, $"from-a-{s}"))];
        }

        var batch = a.GetOrAddAsync<string, int>([new(x, 1), new(y, 2), new(z, 3)], Generate, Ttl, null, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        (await b.GetOrAddAsync<string>(y, _ => Task.FromResult<string?>("from-b"), Ttl, Ct)).Should().Be("from-b");
        release.SetResult();

        var result = await batch;

        result.Single(r => r.Key == 1).Value.Should().Be("from-a-1");
        result.Single(r => r.Key == 2).Value.Should().Be("from-b", "the key another node stored first is the answer");
        result.Single(r => r.Key == 3).Value.Should().Be("from-a-3");
        (await Raw.StringGetAsync(Find(x))).ToString().Should().Contain("from-a-1");
        (await Raw.StringGetAsync(Find(y))).ToString().Should().Contain("from-b").And.NotContain("from-a-2");
        (await Raw.StringGetAsync(Find(z))).ToString().Should().Contain("from-a-3");
        foreach (var key in new[] { x, y, z })
        {
            (await Raw.KeyTimeToLiveAsync(Find(key)))!.Value.Should().BeCloseTo(Ttl, TimeSpan.FromSeconds(15));
        }

        (await a.GetAsync<string>(y, Ct)).Should().Be("from-b", "the stored value is kept locally");
    }
    [Fact]
    public async Task A_chunked_hash_fill_is_renamed_under_an_absent_key_and_leaves_a_held_one()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var a = Hashes("a");
        var b = Hashes("b");
        var many = Enumerable.Range(0, 8).ToDictionary(i => $"f{i}", i => (string?)$"v{i}");
        var absent = (CacheKey)$"it-{Guid.NewGuid():N}";
        var held = (CacheKey)$"it-{Guid.NewGuid():N}";

        (await a.GetOrAddAsync<string>(absent, _ => Task.FromResult<IDictionary<string, string?>>(many), Ttl, Ct)).Should().BeEquivalentTo(many);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IDictionary<string, string?>> Generate(CancellationToken _)
        {
            started.SetResult();
            await release.Task;
            return many;
        }

        var racing = a.GetOrAddAsync<string>(held, Generate, Ttl, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        (await b.GetOrAddAsync<string>(held, _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["z"] = "0" }), Ttl, Ct)).Should().ContainKey("z");
        release.SetResult();

        (await racing).Should().BeEquivalentTo(new Dictionary<string, string?> { ["z"] = "0" }, "the stored hash is the answer");

        (await b.GetAsync<string>(absent, Ct)).Should().BeEquivalentTo(many);
        (await Raw.HashExistsAsync(HashKey(held), "f0")).Should().BeFalse("the fill did not merge into the held hash");
        (await Raw.HashExistsAsync(HashKey(held), "z")).Should().BeTrue();
        (await Raw.KeyTimeToLiveAsync(HashKey(held)))!.Value.Should().BeCloseTo(Ttl, TimeSpan.FromSeconds(15));
        Server().Keys(pattern: $"*{_app}*stage*").Should().BeEmpty("the staging key of the lost fill is dropped");
    }

    [Fact]
    public async Task A_hash_fill_whose_deadline_passed_leaves_the_hash_a_peer_stored()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var inner = Tier();
        var key = (CacheKey)$"it-{Guid.NewGuid():N}";
        (await inner.SetVersionedAsync<string>(key, new Dictionary<string, string?> { ["z"] = "0" }, new HashCacheEntryOptions(ExpireTime: DateTimeOffset.UtcNow.AddMinutes(5)), null, new PeerFieldLimits(8, 1024), Ct)).Written.Should().BeTrue();

        var filled = await inner.FillVersionedAsync<string>(key, new Dictionary<string, string?> { ["a"] = "1" }, new HashCacheEntryOptions(ExpireTime: DateTimeOffset.UtcNow.AddSeconds(-1)), null, new PeerFieldLimits(8, 1024), Ct);

        filled.Written.Should().BeFalse();
        (await Raw.HashExistsAsync(HashKey(key), "z")).Should().BeTrue("a fill of an absent key deletes nothing it did not find absent");
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }

        _raw?.Dispose();
        GC.SuppressFinalize(this);
    }

    private IServer Server()
    {
        _ = Raw;
        return _raw!.GetServer(_raw.GetEndPoints()[0]);
    }

    private RedisKey Find(string key) => Server().Keys(pattern: $"*{key}*").Single();

    private RedisKey HashKey(CacheKey key) => Find(key.Name);

    private ICache Node(string source, bool scripts) =>
        Provider(source, o => o.ReadScriptsEnabled = scripts).GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemoryRedis);

    private ISharedHashTier Tier() =>
        (ISharedHashTier)Provider("t", o => o.HashFieldBatchSize = 3).GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.Redis);

    private IHashCache Hashes(string source) =>
        Provider(source, o => o.HashFieldBatchSize = 3).GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.InMemoryRedis);

    private ServiceProvider Provider(string source, Action<RedisCacheOptions> redis)
    {
        var provider = new ServiceCollection()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(redis);
                    b.AddBroadcast();
                    b.AddInMemoryRedis(o => o.LocalMaxExpiration = TimeSpan.FromMinutes(1));
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.SourceUri = new Uri($"urn:{source}-{_app}");
                })
            .BuildServiceProvider();
        _nodes.Add(provider);
        return provider;
    }
}
