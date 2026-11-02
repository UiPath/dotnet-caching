using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class HashReplaceIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private readonly string _app = $"hr{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_merge_keeps_the_other_fields_on_the_writer_and_the_peer()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Hashes("a");
        var peer = Hashes("b");
        var key = (CacheKey)$"it-{Guid.NewGuid():N}";
        (await writer.SetAsync<string>(key, new Dictionary<string, string?> { ["a"] = "1", ["b"] = "2" }, Ct)).Should().BeTrue();
        (await peer.GetAsync<string>(key, Ct)).Should().HaveCount(2);

        (await writer.SetAsync<string>(key, new Dictionary<string, string?> { ["a"] = "9" }, new HashCacheEntryOptions(SetOption: HashCacheSetOption.HashReplace), Ct)).Should().BeTrue();

        (await writer.GetAsync<string>(key, Ct)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "9", ["b"] = "2" });
        var until = TimeProvider.System.GetUtcNow().AddSeconds(5);
        while (!((await peer.GetAsync<string>(key, Ct)).TryGetValue("a", out var seen) && seen == "9") && TimeProvider.System.GetUtcNow() < until)
        {
            await Task.Delay(20, Ct);
        }

        (await peer.GetAsync<string>(key, Ct)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "9", ["b"] = "2" });
    }

    [Fact]
    public async Task A_merge_on_a_node_that_holds_nothing_reads_the_whole_hash_next()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var first = Hashes("a");
        var second = Hashes("b");
        var key = (CacheKey)$"it-{Guid.NewGuid():N}";
        (await first.SetAsync<string>(key, new Dictionary<string, string?> { ["a"] = "1", ["b"] = "2" }, Ct)).Should().BeTrue();

        (await second.SetAsync<string>(key, new Dictionary<string, string?> { ["a"] = "9" }, new HashCacheEntryOptions(SetOption: HashCacheSetOption.HashReplace), Ct)).Should().BeTrue();

        (await second.GetAsync<string>(key, Ct)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "9", ["b"] = "2" });
    }

    [Fact]
    public async Task RemoveFields_removes_only_those_fields_and_the_peer_drops_its_copy()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Hashes("a");
        var peer = Hashes("b");
        var key = (CacheKey)$"it-{Guid.NewGuid():N}";
        (await writer.SetAsync<string>(key, new Dictionary<string, string?> { ["a"] = "1", ["b"] = "2", ["c"] = "3" }, Ct)).Should().BeTrue();
        (await peer.GetAsync<string>(key, Ct)).Should().HaveCount(3);

        (await writer.RemoveFieldsAsync<string>(key, ["b"], Ct)).Should().BeTrue();

        (await writer.GetAsync<string>(key, Ct)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "1", ["c"] = "3" });
        var until = DateTime.UtcNow.AddSeconds(10);
        while ((await peer.GetAsync<string>(key, Ct)).Count != 2 && DateTime.UtcNow < until)
        {
            await Task.Delay(20, Ct);
        }

        (await peer.GetAsync<string>(key, Ct)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "1", ["c"] = "3" });
    }

    [Fact]
    public async Task Removing_every_field_removes_the_hash_and_a_long_list_goes_in_batches()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Hashes("a", fieldBatch: 100);
        var key = (CacheKey)$"it-{Guid.NewGuid():N}";
        var fields = Enumerable.Range(0, 1050).Select(i => $"f{i}").ToArray();
        (await cache.SetAsync<string>(key, fields.ToDictionary(f => f, f => (string?)f), Ct)).Should().BeTrue();

        (await cache.RemoveFieldsAsync<string>(key, fields, Ct)).Should().BeTrue();

        (await cache.ContainsAsync<string>(key, Ct)).Should().BeFalse();
        (await cache.GetAsync<string>(key, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_hash_of_many_fields_is_written_replaced_merged_and_read_back_whole()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Hashes("a", fieldBatch: 100);
        var key = (CacheKey)$"it-{Guid.NewGuid():N}";
        var many = Enumerable.Range(0, 5000).ToDictionary(i => $"f{i}", i => (string?)$"v{i}");
        var ttl = TimeSpan.FromMinutes(10);

        (await cache.SetAsync<string>(key, many, ttl, Ct)).Should().BeTrue();
        (await cache.GetAsync<string>(key, Ct)).Should().HaveCount(5000);
        (await cache.GetAsync<string>(key, ["f4999", "f0", "f2500"], Ct)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["f4999"] = "v4999", ["f0"] = "v0", ["f2500"] = "v2500" });
        (await cache.GetCacheEntryAsync<string>(key, Ct)).Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow + ttl, TimeSpan.FromSeconds(30));

        var smaller = Enumerable.Range(0, 3000).ToDictionary(i => $"g{i}", i => (string?)$"w{i}");
        (await cache.SetAsync<string>(key, smaller, ttl, Ct)).Should().BeTrue();
        var replaced = await cache.GetAsync<string>(key, Ct);
        replaced.Should().HaveCount(3000, "a replacement leaves none of the old fields");
        replaced.Keys.Should().OnlyContain(k => k.StartsWith('g'));

        var merge = Enumerable.Range(0, 2500).ToDictionary(i => $"m{i}", i => (string?)$"x{i}");
        (await cache.SetAsync<string>(key, merge, new HashCacheEntryOptions(TimeToLive: ttl, SetOption: HashCacheSetOption.HashReplace), Ct)).Should().BeTrue();
        (await cache.GetAsync<string>(key, Ct)).Should().HaveCount(5500);
    }

    [Fact]
    public async Task A_replacement_in_chunks_of_a_hash_with_no_expiry_lands_with_no_expiry()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Hashes("a", fieldBatch: 100);
        var key = (CacheKey)$"it-{Guid.NewGuid():N}";
        var many = Enumerable.Range(0, 1000).ToDictionary(i => $"f{i}", i => (string?)$"v{i}");

        (await cache.SetAsync<string>(key, many, DateTimeOffset.MaxValue, Ct)).Should().BeTrue();

        (await cache.GetAsync<string>(key, Ct)).Should().HaveCount(1000);
        (await cache.TimeToLiveAsync<string>(key, Ct)).Should().BeNull("the staging key's orphan lifetime must not reach the key");
    }

    [Theory]
    [InlineData(HashCacheSetOption.HashReplace)]
    [InlineData(HashCacheSetOption.KeyReplace)]
    public async Task A_chunked_write_whose_deadline_passes_midway_leaves_no_hash_without_an_expiry(HashCacheSetOption setOption)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Hashes("a", fieldBatch: 50);
        var run = Guid.NewGuid().ToString("N");
        var fields = Enumerable.Range(0, 2000).ToDictionary(i => $"f{i}", i => (string?)$"v{i}");
        for (var round = 0; round < 20; round++)
        {
            var deadline = TimeProvider.System.GetUtcNow().AddMilliseconds(2);
            await cache.SetAsync<string>($"it-{run}-{round}", fields, new HashCacheEntryOptions(ExpireTime: deadline, SetOption: setOption), Ct);
        }

        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var server = redis.GetServers()[0];
        await foreach (var key in server.KeysAsync(pattern: $"*{run}*"))
        {
            // A null TTL also answers a key that expired after the scan listed it; only one still there without a TTL is the defect.
            var ttl = await redis.GetDatabase().KeyTimeToLiveAsync(key);
            (ttl is not null || !await redis.GetDatabase().KeyExistsAsync(key)).Should().BeTrue("{0} must expire or be gone", key);
        }
    }

    [Fact]
    public async Task A_replacement_whose_staging_key_is_gone_before_the_rename_leaves_the_old_hash_and_its_expiry()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Hashes("a", fieldBatch: 100);
        var key = (CacheKey)$"it-{Guid.NewGuid():N}";
        var old = Enumerable.Range(0, 300).ToDictionary(i => $"o{i}", i => (string?)$"v{i}");
        (await cache.SetAsync<string>(key, old, TimeSpan.FromMinutes(10), Ct)).Should().BeTrue();
        ((UiPath.Caching.Redis.RedisHashCache)cache).OnStaged = static (database, staging) => database.KeyDeleteAsync(staging);

        (await cache.SetAsync<string>(key, Enumerable.Range(0, 300).ToDictionary(i => $"n{i}", i => (string?)$"w{i}"), DateTimeOffset.MaxValue, Ct)).Should().BeFalse();
        ((UiPath.Caching.Redis.RedisHashCache)cache).OnStaged = null;
        (await cache.GetAsync<string>(key, Ct)).Keys.Should().OnlyContain(k => k.StartsWith('o'));
        (await cache.TimeToLiveAsync<string>(key, Ct)).Should().NotBeNull("a missing staging key must abort the PERSIST too");
    }

    [Fact]
    public async Task The_redis_hash_cache_serves_the_span_members_by_the_key_s_text()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Hashes("a", fieldBatch: 100);
        var name = $"it-{Guid.NewGuid():N}";
        var fields = new Dictionary<string, string?> { ["f"] = "v", ["g"] = "w" };
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);

        (await cache.SetAsync<string>($"{name}", fields, null, Ct)).Should().BeTrue();
        (await cache.SetAsync<string>($"{name}", fields, TimeSpan.FromMinutes(10), null, Ct)).Should().BeTrue();
        (await cache.SetAsync<string>($"{name}", fields, expires, null, Ct)).Should().BeTrue();
        (await cache.SetAsync<string>($"{name}", fields, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(10)), null, Ct)).Should().BeTrue();
        (await cache.ContainsAsync<string>($"{name}", Ct)).Should().BeTrue();
        (await cache.GetItemAsync<string>($"{name}", "f", null, Ct)).Should().Be("v");
        (await cache.GetAsync<string>($"{name}", null, Ct)).Should().BeEquivalentTo(fields);
        (await cache.GetCacheEntryAsync<string>($"{name}", null, Ct)).Value.Should().BeEquivalentTo(fields);
        (await cache.GetOrAddAsync<string>($"{name}", _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?>()), null, Ct)).Should().BeEquivalentTo(fields);
        (await cache.GetOrAddAsync<string>($"{name}", _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?>()), TimeSpan.FromMinutes(10), null, Ct)).Should().BeEquivalentTo(fields);
        (await cache.GetOrAddAsync<string>($"{name}", _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?>()), expires, null, Ct)).Should().BeEquivalentTo(fields);
        (await cache.GetOrAddAsync<string>($"{name}", _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?>()), expires, HashCacheSetOption.KeyReplace, null, Ct)).Should().BeEquivalentTo(fields);
        (await cache.RemoveManyAsync<string>([name], Ct)).Should().BeEmpty();
        (await cache.ContainsAsync<string>(name, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_hash_beyond_the_paging_threshold_is_read_by_pages()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Hashes("a", fieldBatch: 50);
        var key = (CacheKey)$"it-{Guid.NewGuid():N}";
        var many = Enumerable.Range(0, 2000).ToDictionary(i => $"f{i}", i => (string?)$"v{i}");
        (await cache.SetAsync<string>(key, many, TimeSpan.FromMinutes(10), Ct)).Should().BeTrue();

        var entry = await cache.GetCacheEntryAsync<string>(key, Ct);

        entry.Value.Should().BeEquivalentTo(many);
        entry.Expiration.Should().BeAfter(DateTimeOffset.UtcNow);
        (await cache.GetAsync<string>(key, Ct)).Should().BeEquivalentTo(many);
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private IHashCache Hashes(string source, int fieldBatch = 1000)
    {
        var provider = new ServiceCollection()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(o => o.HashFieldBatchSize = fieldBatch);
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
        return provider.GetRequiredService<ICacheFactory>().CreateHashCache(fieldBatch == 1000 ? KnownCacheProviderNames.InMemoryRedis : KnownCacheProviderNames.Redis);
    }
}
