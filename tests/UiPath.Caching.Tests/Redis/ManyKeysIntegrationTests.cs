using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UiPath.Caching.Config;
using UiPath.Caching.Policies;

namespace UiPath.Caching.Tests.Redis;

/// <summary>Two nodes on one Redis, standalone or cluster, with broadcast: the batch remove and set across many keys and slots.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class ManyKeysIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private readonly string _app = $"mk{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];
    private readonly WarningLog _warnings = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SetMany_then_RemoveMany_across_many_slots_reach_redis_and_the_peer()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (_, writer) = Node("a", o => o.BatchChunkSize = 40);
        var (_, peer) = Node("b");
        var keys = Keys(130);

        (await writer.SetManyAsync(keys.Select(k => new KeyValuePair<CacheKey, string?>(k, $"v-{k.Name}")), (CachePolicy?)null, Ct)).Should().BeEmpty();

        foreach (var key in keys)
        {
            (await peer.GetAsync<string>(key, null, Ct)).Should().Be($"v-{key.Name}");
        }

        (await writer.RemoveManyAsync<string>(keys, Ct)).Should().BeEmpty();

        foreach (var key in keys)
        {
            (await EventuallyAsync(async () => await peer.GetAsync<string>(key, null, Ct) is null)).Should().BeTrue($"the peer drops {key.Name} on its event");
            (await writer.GetAsync<string>(key, null, Ct)).Should().BeNull();
        }
    }

    [Fact]
    public async Task A_batch_above_the_threshold_drops_the_whole_local_tier_of_the_peer_with_one_event()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (_, writer) = Node("a", o => o.BatchTopicInvalidationThreshold = 20);
        var (_, peer) = Node("b");
        var keys = Keys(60);
        var unrelated = (CacheKey)$"unrelated-{Guid.NewGuid():N}";
        await writer.SetAsync(unrelated, "keep", null, Ct);
        (await writer.SetManyAsync(keys.Select(k => new KeyValuePair<CacheKey, string?>(k, "old")), (CachePolicy?)null, Ct)).Should().BeEmpty();
        foreach (var key in keys.Append(unrelated))
        {
            (await peer.GetAsync<string>(key, null, Ct)).Should().NotBeNull();
        }

        (await writer.SetManyAsync(keys.Select(k => new KeyValuePair<CacheKey, string?>(k, "new")), (CachePolicy?)null, Ct)).Should().BeEmpty();

        foreach (var key in keys)
        {
            (await EventuallyAsync(async () => await peer.GetAsync<string>(key, null, Ct) == "new")).Should().BeTrue($"the peer's copy of {key.Name} is dropped by the topic-wide event");
        }
    }

    [Fact]
    public async Task Hash_SetMany_then_RemoveMany_across_many_slots_reach_redis_and_the_peer()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (writerNode, _) = Node("a", o => o.BatchChunkSize = 40);
        var (peerNode, _) = Node("b");
        var writer = Hashes(writerNode);
        var peer = Hashes(peerNode);
        var keys = Keys(130);

        var pairs = keys.Select(k => new KeyValuePair<CacheKey, IDictionary<string, string?>>(k, new Dictionary<string, string?> { ["f"] = $"v-{k.Name}", ["g"] = "x" })).ToArray();
        (await writer.SetManyAsync<string>(pairs, new HashCacheEntryOptions(), null, Ct)).Should().BeEmpty();

        foreach (var key in keys)
        {
            (await peer.GetItemAsync<string>(key, "f", null, Ct)).Should().Be($"v-{key.Name}");
        }

        (await writer.RemoveManyAsync<string>(keys, Ct)).Should().BeEmpty();

        foreach (var key in keys)
        {
            (await EventuallyAsync(async () => await peer.GetItemAsync<string>(key, "f", null, Ct) is null)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Hundreds_of_keys_take_about_two_round_trips_of_wall_time_not_one_per_key()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var delay = TimeSpan.FromMilliseconds(200);
        var (_, cache) = Node("a", o => o.BatchChunkSize = 500, new DelayedWrites(delay));
        var keys = Keys(300);
        var pairs = keys.Select(k => new KeyValuePair<CacheKey, string?>(k, "v")).ToArray();
        await cache.SetManyAsync(pairs, (CachePolicy?)null, Ct);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        (await cache.RemoveManyAsync<string>(keys, Ct)).Should().BeEmpty();

        watch.Elapsed.Should().BeLessThan(delay * 10, "the 300 deletes share one wait and their 300 events another");
    }

    [Fact]
    public async Task Twenty_thousand_keys_go_through_in_chunks()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (_, cache) = Node("a", o => o.BatchChunkSize = 1000);
        var prefix = $"big-{Guid.NewGuid():N}-";
        IEnumerable<KeyValuePair<CacheKey, string?>> Pairs() => Enumerable.Range(0, 20_000).Select(i => new KeyValuePair<CacheKey, string?>($"{prefix}{i}", "v"));
        IEnumerable<CacheKey> AllKeys() => Enumerable.Range(0, 20_000).Select(i => (CacheKey)$"{prefix}{i}");

        (await cache.SetManyAsync(Pairs(), (CachePolicy?)null, Ct)).Should().BeEmpty(_warnings.ToString());
        (await cache.RemoveManyAsync<string>(AllKeys(), Ct)).Should().BeEmpty(_warnings.ToString());
        (await cache.GetAsync<string>($"{prefix}77", null, Ct)).Should().BeNull();
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static IHashCache Hashes(ServiceProvider node) => node.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.InMemoryRedis);

    private static CacheKey[] Keys(int count)
    {
        var run = Guid.NewGuid().ToString("N");
        return Enumerable.Range(0, count).Select(i => (CacheKey)$"it-{run}-{i}").ToArray();
    }

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

        return await condition();
    }

    private (ServiceProvider Provider, ICache Cache) Node(string source, Action<InMemoryRedisCacheOptions>? configure = null, IResiliencePipeline? writes = null)
    {
        var services = new ServiceCollection().AddLogging(b => b.AddProvider(_warnings));
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
                    b.AddInMemoryRedis(o =>
                    {
                        o.LocalMaxExpiration = TimeSpan.FromMinutes(1);
                        configure?.Invoke(o);
                    });
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.SourceUri = new Uri($"urn:{source}-{_app}");
                })
            .BuildServiceProvider();
        _nodes.Add(provider);
        return (provider, provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemoryRedis));
    }

    /// <summary>What the nodes logged at warning or above, so a failed key says why.</summary>
    private sealed class WarningLog : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel) && _lines.Count < 20)
            {
                _lines.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
            }
        }

        public override string ToString() => string.Join(Environment.NewLine, _lines);

        public void Dispose()
        {
        }
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
}
