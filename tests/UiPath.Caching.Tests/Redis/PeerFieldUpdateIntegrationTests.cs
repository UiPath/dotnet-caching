using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using StackExchange.Redis.Profiling;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class PeerFieldUpdateIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private static readonly string[] InnerReadCommands = ["EXEC", "EVAL_RO", "EVALSHA_RO", "HGETALL"];

    private readonly string _app = $"pf{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HashCacheEntryOptions Merge => new(SetOption: HashCacheSetOption.HashReplace);

    [Fact]
    public async Task A_peer_serves_a_written_field_from_memory_without_reading_the_hash_and_the_key_keeps_its_ttl()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", peerFieldUpdates: true);
        var peer = Node("b", peerFieldUpdates: true);
        await WarmAsync(writer, peer);
        var key = Unique();
        (await writer.Hashes.SetAsync<string>(key, Fields(("a", "1"), ("b", "2")), TimeSpan.FromMinutes(10), Ct)).Should().BeTrue();
        _ = peer.Profiler.Session.FinishProfiling().ToList();
        (await peer.Hashes.GetAsync<string>(key, Ct)).Should().HaveCount(2);
        await EventsConsumedAsync(peer);
        (await peer.Hashes.GetAsync<string>(key, Ct)).Should().HaveCount(2);
        _ = peer.Profiler.Session.FinishProfiling().ToList();

        (await writer.Hashes.SetAsync<string>(key, Fields(("c", "3")), new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(10), SetOption: HashCacheSetOption.HashReplace), Ct)).Should().BeTrue();
        var seen = await UntilAsync(async () => (await peer.Hashes.GetAsync<string>(key, Ct)).ContainsKey("c"));

        seen.Should().BeTrue("the peer's event is applied to its copy");
        (await peer.Hashes.GetAsync<string>(key, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("c", "3")));
        peer.Profiler.Session.FinishProfiling().Select(c => c.Command).Should().NotContain(InnerReadCommands);
        (await peer.Redis.TimeToLiveAsync<string>(key, Ct)).Should().BeGreaterThan(TimeSpan.Zero);
        (await Counted(key)).Should().Be(2);
    }

    [Fact]
    public async Task Two_nodes_merging_and_applying_on_the_same_hashes_under_load_never_stall()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var nodes = new[] { Node("a", peerFieldUpdates: true), Node("b", peerFieldUpdates: true) };
        await WarmAsync(nodes[0], nodes[1]);
        var keys = Enumerable.Range(0, 20).Select(_ => Unique()).ToArray();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // An update applied under the change token's lock waited for the merge lock, while a read keeping its value held the merge lock
        // and waited to register on that token: every caller of the node then stalled behind the two.
        async Task Work(int w)
        {
            var node = nodes[w % 2];
            var random = new Random(w);
            while (!stop.IsCancellationRequested)
            {
                var key = keys[random.Next(keys.Length)];
                if ((await node.Hashes.GetAsync<string>(key, Ct)).Count == 0 || random.Next(4) == 0)
                {
                    await node.Hashes.SetAsync<string>(key, Fields(($"ip:{random.Next(250)}", "1")), new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(10), SetOption: HashCacheSetOption.HashReplace), Ct);
                }
            }
        }

        var workers = Enumerable.Range(0, 32).Select(w => Task.Run(() => Work(w), Ct)).ToArray();

        var all = Task.WhenAll(workers);
        (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(40), Ct))).Should().BeSameAs(all, "every caller returns once the load stops");
        await all;
    }

    [Fact]
    public async Task Two_nodes_writing_merges_in_the_background_and_awaited_end_with_every_kept_copy_equal_to_the_hash()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var nodes = new[] { Node("a", peerFieldUpdates: true), Node("b", peerFieldUpdates: true) };
        await WarmAsync(nodes[0], nodes[1]);
        var keys = Enumerable.Range(0, 6).Select(_ => Unique()).ToArray();
        var background = new CachePolicy { BackgroundDistributedWrite = true };

        // A few field names that every writer shares, so the order the writes took in the hash decides what each copy must hold.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1994:\"for\" loop increment clauses should modify the loops' counters", Justification = "The loop runs until cancelled; the counter only numbers the writes.")]
        async Task Work(int round, int w, CancellationToken stop)
        {
            var node = nodes[w % 2];
            var random = new Random(round * 100 + w);
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                var key = keys[random.Next(keys.Length)];
                if (random.Next(3) == 0)
                {
                    await node.Hashes.GetAsync<string>(key, Ct);
                    continue;
                }

                var fields = Fields(($"f{random.Next(5)}", $"{round}:{w}:{i}"));
                var options = new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(10), SetOption: random.Next(10) == 0 ? HashCacheSetOption.KeyReplace : HashCacheSetOption.HashReplace);
                await node.Hashes.SetAsync<string>(key, fields, options, random.Next(2) == 0 ? background : null, Ct);
            }
        }

        async Task<bool> Agree()
        {
            foreach (var key in keys)
            {
                var truth = await nodes[0].Redis.GetAsync<string>(key, Ct);
                foreach (var node in nodes)
                {
                    var kept = await node.Hashes.GetAsync<string>(key, Ct);
                    if (kept.Count != truth.Count || kept.Any(f => !truth.TryGetValue(f.Key, out var v) || v != f.Value))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        for (var round = 0; round < 8; round++)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Task.WhenAll(Enumerable.Range(0, 16).Select(w => Task.Run(() => Work(round, w, stop.Token), Ct)));

            (await UntilAsync(Agree)).Should().BeTrue($"once the writes and their events of round {round} are done, no node keeps a copy that differs from the hash");
        }
    }

    [Fact]
    public async Task A_write_whose_version_is_at_the_64_bit_maximum_still_lands_with_its_ttl_and_the_peer_drops_its_copy()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", peerFieldUpdates: true);
        var peer = Node("b", peerFieldUpdates: true);
        await WarmAsync(writer, peer);
        var key = Unique();
        (await writer.Hashes.SetAsync<string>(key, Fields(("a", "1")), TimeSpan.FromMinutes(10), Ct)).Should().BeTrue();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var composed = default(RedisKey);
        await foreach (var found in redis.GetServers()[0].KeysAsync(pattern: $"*{key}"))
        {
            composed = found;
        }

        await redis.GetDatabase().HashSetAsync(composed, "_version_", long.MaxValue);
        (await peer.Hashes.GetAsync<string>(key, Ct)).Should().HaveCount(1);
        await EventsConsumedAsync(peer);

        (await writer.Hashes.SetAsync<string>(key, Fields(("b", "2")), new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(10), SetOption: HashCacheSetOption.HashReplace), Ct)).Should().BeTrue();

        (await UntilAsync(async () => (await peer.Hashes.GetAsync<string>(key, Ct)).ContainsKey("b"))).Should().BeTrue("the peer drops its copy and reads the hash again");
        (await redis.GetDatabase().HashExistsAsync(composed, "b")).Should().BeTrue();
        ((long)await redis.GetDatabase().HashGetAsync(composed, "_version_")).Should().Be(long.MaxValue);
        (await redis.GetDatabase().KeyTimeToLiveAsync(composed)).Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task A_counted_write_is_one_transaction_with_the_fields_the_count_and_the_expiry()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", peerFieldUpdates: true);
        var key = Unique();
        await writer.Hashes.SetAsync<string>(key, Fields(("a", "1")), TimeSpan.FromMinutes(10), Ct);
        _ = writer.Profiler.Session.FinishProfiling().ToList();

        await writer.Hashes.SetAsync<string>(key, Fields(("b", "2")), new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(10), SetOption: HashCacheSetOption.HashReplace), Ct);

        var commands = writer.Profiler.Session.FinishProfiling().Select(c => c.Command).ToList();
        commands.Should().ContainSingle(c => c == "EXEC", "the fields, the count and the expiry are one transaction");
        commands.Should().NotContain("HINCRBY", "the count is queued in the transaction, not sent apart");
        (await writer.Redis.TimeToLiveAsync<string>(key, Ct)).Should().BeGreaterThan(TimeSpan.Zero);
        (await Counted(key)).Should().Be(2);
    }

    [Fact]
    public async Task The_version_counts_writes_restarts_with_a_replacement_and_never_reaches_a_caller()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", peerFieldUpdates: true);
        var key = Unique();
        await writer.Hashes.SetAsync<string>(key, Fields(("a", "1")), Ct);
        await writer.Hashes.SetAsync<string>(key, Fields(("b", "2")), Merge, Ct);
        await writer.Hashes.SetAsync<string>(key, Fields(("c", "3")), Merge, Ct);
        (await Counted(key)).Should().Be(3);

        await writer.Hashes.SetAsync<string>(key, Fields(("z", "9")), Ct);

        (await Counted(key)).Should().Be(1);
        foreach (var read in new[] { await writer.Redis.GetAsync<string>(key, Ct), (await writer.Redis.GetCacheEntryAsync<string>(key, Ct)).Value!, await writer.Redis.GetAsync<string>(key, ["z"], Ct) })
        {
            read.Keys.Should().NotContain("_version_");
        }

        await FluentActions.Awaiting(() => writer.Redis.SetAsync<string>(key, Fields(("_version_", "9")), Ct).AsTask()).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => writer.Redis.GetItemAsync<string>(key, "_version_", null, Ct).AsTask()).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => writer.Redis.RemoveFieldsAsync<string>(key, ["_version_"], Ct).AsTask()).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_hash_is_made_with_an_epoch_that_its_later_writes_keep_and_a_hash_made_again_draws_another_with_no_field_added_and_the_ttl_kept()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", peerFieldUpdates: true);
        var key = Unique();
        var ttl = new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(10), SetOption: HashCacheSetOption.HashReplace);

        await writer.Hashes.SetAsync<string>(key, Fields(("a", "1")), ttl, Ct);
        var first = await Epoch(key);
        await writer.Hashes.SetAsync<string>(key, Fields(("b", "2")), ttl, Ct);
        var second = await Epoch(key);
        await writer.Hashes.RemoveAsync<string>(key, Ct);
        (await Raw(key, "_version_")).Should().BeNull();
        await writer.Hashes.SetAsync<string>(key, Fields(("a", "1")), ttl, Ct);
        var again = await Epoch(key);
        await writer.Hashes.SetAsync<string>(key, Fields(("z", "9")), TimeSpan.FromMinutes(10), Ct);
        var replaced = await Epoch(key);

        first.Should().BePositive();
        second.Should().Be(first, "a write to a hash that exists keeps its epoch");
        again.Should().NotBe(first, "a hash made again is another epoch");
        replaced.Should().NotBe(again, "a replacement of the key is another epoch");
        (await Counted(key)).Should().Be(1);
        (await writer.Redis.TimeToLiveAsync<string>(key, Ct)).Should().BeGreaterThan(TimeSpan.Zero);
        (await HashLength(key)).Should().Be(2, "the fields and the count, nothing else");
        (await writer.Redis.GetAsync<string>(key, Ct)).Should().BeEquivalentTo(Fields(("z", "9")));
    }

    [Fact]
    public async Task A_count_an_older_node_left_with_no_epoch_is_counted_on_and_given_none()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", peerFieldUpdates: true);
        var key = Unique();
        var ttl = new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(10), SetOption: HashCacheSetOption.HashReplace);
        await writer.Hashes.SetAsync<string>(key, Fields(("a", "1")), TimeSpan.FromMinutes(10), Ct);
        await SetRaw(key, "_version_", 5);

        await writer.Hashes.SetAsync<string>(key, Fields(("b", "2")), ttl, Ct);

        (await Raw(key, "_version_")).Should().Be(6);
        (await writer.Redis.TimeToLiveAsync<string>(key, Ct)).Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task A_chunked_merge_that_makes_the_hash_draws_an_epoch_and_keeps_its_ttl()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", peerFieldUpdates: true, fieldBatch: 4);
        var key = Unique();
        var fields = Enumerable.Range(0, 10).ToDictionary(i => $"f{i}", i => (string?)$"v{i}");

        (await writer.Hashes.SetAsync<string>(key, fields, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(10), SetOption: HashCacheSetOption.HashReplace), Ct)).Should().BeTrue();

        (await Epoch(key)).Should().BePositive();
        (await Counted(key)).Should().Be(1);
        (await writer.Redis.TimeToLiveAsync<string>(key, Ct)).Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task With_the_option_off_no_count_is_sent_or_left_behind()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", peerFieldUpdates: false);
        var key = Unique();

        await writer.Hashes.SetAsync<string>(key, Fields(("a", "1")), Ct);
        await writer.Hashes.SetAsync<string>(key, Fields(("b", "2")), Merge, Ct);
        await writer.Hashes.RemoveFieldsAsync<string>(key, ["a"], Ct);

        // Server-wide command stats would count other test runs sharing the server; a count would have made the field.
        (await Raw(key, "_version_")).Should().BeNull();
    }

    [Fact]
    public async Task Removing_fields_counts_the_write_and_the_peer_drops_its_copy_and_a_missing_key_is_not_made()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", peerFieldUpdates: true);
        var peer = Node("b", peerFieldUpdates: true);
        var key = Unique();
        await writer.Hashes.SetAsync<string>(key, Fields(("a", "1"), ("b", "2"), ("c", "3")), TimeSpan.FromMinutes(10), Ct);
        (await peer.Hashes.GetAsync<string>(key, Ct)).Should().HaveCount(3);

        (await writer.Hashes.RemoveFieldsAsync<string>(key, ["b"], Ct)).Should().BeTrue();

        (await Counted(key)).Should().Be(2);
        (await UntilAsync(async () => (await peer.Hashes.GetAsync<string>(key, Ct)).Count == 2)).Should().BeTrue();
        (await peer.Redis.TimeToLiveAsync<string>(key, Ct)).Should().BeGreaterThan(TimeSpan.Zero);
        var missing = Unique();

        (await writer.Hashes.RemoveFieldsAsync<string>(missing, ["a"], Ct)).Should().BeTrue();

        (await writer.Redis.ContainsAsync<string>(missing, Ct)).Should().BeFalse("counting must not make a key with no lifetime");
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public async Task Removing_the_last_field_of_a_counted_hash_removes_the_key_and_no_version_is_left_alone(int fields, int fieldBatch)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", peerFieldUpdates: true, fieldBatch: fieldBatch);
        var key = Unique();
        var names = Enumerable.Range(0, fields).Select(i => $"f{i}").ToArray();
        await writer.Hashes.SetAsync<string>(key, names.ToDictionary(n => n, n => (string?)"1"), TimeSpan.FromMinutes(10), Ct);
        await writer.Hashes.SetAsync<string>(key, Fields(("x", "1")), Merge, Ct);

        (await writer.Hashes.RemoveFieldsAsync<string>(key, [.. names, "x"], Ct)).Should().BeTrue();

        (await writer.Redis.ContainsAsync<string>(key, Ct)).Should().BeFalse();
        (await Raw(key, "_version_")).Should().BeNull();
    }

    [Theory]
    [InlineData(HashCacheSetOption.KeyReplace, 1)]
    [InlineData(HashCacheSetOption.HashReplace, 2)]
    public async Task A_chunked_write_counts_once_and_keeps_its_ttl(HashCacheSetOption setOption, long expected)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", peerFieldUpdates: true, fieldBatch: 4);
        var key = Unique();
        await writer.Hashes.SetAsync<string>(key, Fields(("seed", "0")), TimeSpan.FromMinutes(10), Ct);
        var fields = Enumerable.Range(0, 10).ToDictionary(i => $"f{i}", i => (string?)$"v{i}");

        (await writer.Hashes.SetAsync<string>(key, fields, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(10), SetOption: setOption), Ct)).Should().BeTrue();

        (await Counted(key)).Should().Be(expected);
        (await writer.Redis.TimeToLiveAsync<string>(key, Ct)).Should().BeGreaterThan(TimeSpan.Zero);
        (await writer.Redis.GetAsync<string>(key, Ct)).Count.Should().Be(setOption == HashCacheSetOption.KeyReplace ? 10 : 11);
    }

    [Fact]
    public async Task A_hash_read_by_pages_carries_no_version_so_the_peer_drops_its_copy_on_the_next_write()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.ServerVersion >= new Version(7, 0), "Only the read script pages a large hash.");
        var writer = Node("a", peerFieldUpdates: true, fieldBatch: 50);
        var peer = Node("b", peerFieldUpdates: true, fieldBatch: 50);
        var key = Unique();
        var many = Enumerable.Range(0, 2000).ToDictionary(i => $"f{i}", i => (string?)$"v{i}");
        await writer.Hashes.SetAsync<string>(key, many, TimeSpan.FromMinutes(10), Ct);
        (await peer.Hashes.GetAsync<string>(key, Ct)).Should().HaveCount(2000);
        _ = peer.Profiler.Session.FinishProfiling().ToList();

        await writer.Hashes.SetAsync<string>(key, Fields(("extra", "1")), new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(10), SetOption: HashCacheSetOption.HashReplace), Ct);

        (await UntilAsync(async () => (await peer.Hashes.GetAsync<string>(key, Ct)).ContainsKey("extra"))).Should().BeTrue();
        peer.Profiler.Session.FinishProfiling().Select(c => c.Command).Should().Contain(c => c == "HSCAN" || InnerReadCommands.Contains(c), "a copy read by pages has no version to apply a write to");
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // A node's first subscription to a topic starts while its first read returns, and the gap it reports clears what the node holds.
    private static async Task WarmAsync(Peer writer, Peer node)
    {
        var key = Unique();
        await writer.Hashes.SetAsync<string>(key, Fields(("w", "1")), TimeSpan.FromMinutes(10), Ct);
        await node.Hashes.GetAsync<string>(key, Ct);
        var seen = new List<string>();
        (await UntilAsync(() =>
        {
            seen.AddRange(node.Profiler.Session.FinishProfiling().Select(c => c.Command));
            return Task.FromResult(seen.Contains("XREADGROUP"));
        })).Should().BeTrue("the node's reader of the topic is up");
        await Task.Delay(300, Ct);
    }

    // The write that made the hash is announced too: its event drops a copy read before it arrived, so the test starts from a copy read after it.
    private static async Task EventsConsumedAsync(Peer node)
    {
        var seen = new List<string>();
        (await UntilAsync(() =>
        {
            seen.AddRange(node.Profiler.Session.FinishProfiling().Select(c => c.Command));
            return Task.FromResult(seen.Contains("XACK"));
        })).Should().BeTrue("the node consumed the event of the write that made the hash");
        await Task.Delay(100, Ct);
    }

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    private static Dictionary<string, string?> Fields(params (string Field, string Value)[] fields) =>
        fields.ToDictionary(f => f.Field, f => (string?)f.Value);

    private static async Task<bool> UntilAsync(Func<Task<bool>> condition)
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

    private async Task<long?> Counted(string key) =>
        await Raw(key, "_version_") & ((1L << HashVersion.CounterBits) - 1);

    private async Task<long> Epoch(string key) =>
        (await Raw(key, "_version_") ?? 0) >> HashVersion.CounterBits;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1751:Loops with at most one iteration should be refactored", Justification = "The first key matching the pattern is the one wanted.")]
    private async Task<long> HashLength(string key)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        await foreach (var found in redis.GetServers()[0].KeysAsync(pattern: $"*{key}"))
        {
            return await redis.GetDatabase().HashLengthAsync(found);
        }

        return 0;
    }

    private async Task SetRaw(string key, string field, long value)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        await foreach (var found in redis.GetServers()[0].KeysAsync(pattern: $"*{key}"))
        {
            await redis.GetDatabase().HashSetAsync(found, field, value);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1751:Loops with at most one iteration should be refactored", Justification = "The first key matching the pattern is the one wanted.")]
    private async Task<long?> Raw(string key, string field)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var server = redis.GetServers()[0];
        await foreach (var found in server.KeysAsync(pattern: $"*{key}"))
        {
            var value = await redis.GetDatabase().HashGetAsync(found, field);
            return value.IsNull ? null : (long)value;
        }

        return null;
    }

    private Peer Node(string source, bool peerFieldUpdates, int fieldBatch = 1000)
    {
        var profiler = new CommandCountingProfiler();
        var provider = new ServiceCollection()
            .AddSingleton<IRedisProfiler>(profiler)
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o =>
                    {
                        o.ConnectionString = fixture.ConnectionString;
                        o.ProfilerEnabled = true;
                        o.ProfilingSessionFactory = () => profiler.Session;
                    });
                    b.AddRedis(o => o.HashFieldBatchSize = fieldBatch);
                    b.AddBroadcast();
                    b.AddInMemoryRedis(o => o.PeerFieldUpdates = peerFieldUpdates);
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.SourceUri = new Uri($"urn:{source}-{_app}");
                })
            .BuildServiceProvider();
        _nodes.Add(provider);
        var factory = provider.GetRequiredService<ICacheFactory>();
        return new Peer(factory.CreateHashCache(KnownCacheProviderNames.InMemoryRedis), factory.CreateHashCache(KnownCacheProviderNames.Redis), profiler);
    }

    private sealed record Peer(IHashCache Hashes, IHashCache Redis, CommandCountingProfiler Profiler);

    private sealed class CommandCountingProfiler : IRedisProfiler
    {
        public ProfilingSession Session { get; } = new();

        public int Count => 0;

        public ProfilingSession? GetSession() => Session;

        public IDisposable CreateSession(string? sessionId) => NullRedisProfiler.Instance.CreateSession(sessionId);
    }
}
