using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class FillPrimitivesRedisIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly string _app = $"fp{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];
    private ConnectionMultiplexer? _raw;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Raw => (_raw ??= ConnectionMultiplexer.Connect(fixture.ConnectionString)).GetDatabase();

    private string? RestrictedConnection { get; set; }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fill_and_a_found_value_announce_nothing_and_leave_a_peer_copy_alone(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", scripts);
        var peer = Node("b", scripts);
        await WarmAsync(writer, peer);
        var key = Unique();
        (await peer.SetAsync(key, "old", Ct)).Should().BeTrue();
        (await peer.GetAsync<string>(key, Ct)).Should().Be("old");
        await Raw.KeyDeleteAsync(await FindAsync(key));

        Sans(await writer.TryFillAsync(key, "new", Ttl, Ct)).Should().Be(new FillResult<string>(FillOutcome.Added, null));
        Sans(await writer.TryFillAsync(key, "other", Ttl, Ct)).Should().Be(new FillResult<string>(FillOutcome.Existed, "new"));

        await SettledAsync(writer, peer);
        (await peer.GetAsync<string>(key, Ct)).Should().Be("old", "an event would have refreshed or dropped the peer's copy");
        (await Raw.StringGetAsync(await FindAsync(key))).ToString().Should().Contain("new");
        (await Raw.KeyTimeToLiveAsync(await FindAsync(key)))!.Value.Should().BeCloseTo(Ttl, TimeSpan.FromSeconds(15));

        (await writer.SetAsync(key, "set", Ct)).Should().BeTrue();
        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "set")).Should().BeTrue("a write does announce, so the silence above is the fill's");
    }

    [Fact]
    public async Task A_fill_does_not_wait_for_a_generator_of_the_key_in_this_process()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a");
        var key = Unique();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Generate(CancellationToken ct)
        {
            started.SetResult();
            await release.Task;
            return "generated";
        }

        var running = writer.GetOrAddAsync<string>(key, Generate, Ttl, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var fill = await writer.TryFillAsync(key, "filled", Ttl, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        fill.Outcome.Should().Be(FillOutcome.Added);
        release.SetResult();
        await running;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_peer_event_during_the_call_keeps_a_found_value_out_of_the_local_tier(bool pubsub)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", pubsub: pubsub);
        var peer = Node("b", pubsub: pubsub);
        await WarmAsync(writer, peer);
        var key = Unique();
        (await peer.SetAsync(key, "old", Ct)).Should().BeTrue();
        await SettledAsync(writer, peer);
        await ArmAsync(writer, peer);
        var racing = new DelegateComparer(() =>
        {
            PeerWritesAndWriterHears(writer, peer, key, rewrite: "new");
            return true;
        });

        var result = await writer.SetIfChangedAsync(key, "old", racing, Ttl, Ct);

        result.Should().Be(new ChangeResult<string>(ChangeOutcome.Unchanged, "old"));
        (await writer.GetAsync<string>(key, Ct)).Should().Be("new", "the stale value was not installed over the peer's write");
    }

    [Fact]
    public async Task Set_if_changed_fills_quietly_leaves_an_equal_value_untouched_and_announces_a_replacement()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a");
        var peer = Node("b");
        await WarmAsync(writer, peer);
        var key = Unique();
        (await peer.SetAsync(key, "old", Ct)).Should().BeTrue();
        (await peer.GetAsync<string>(key, Ct)).Should().Be("old");
        var redisKey = await FindAsync(key);
        await Raw.KeyDeleteAsync(redisKey);

        Sans(await writer.SetIfChangedAsync(key, "first", StringComparer.Ordinal, TimeSpan.FromSeconds(60), Ct)).Should().Be(new ChangeResult<string>(ChangeOutcome.Filled, "first"));
        await SettledAsync(writer, peer);
        (await peer.GetAsync<string>(key, Ct)).Should().Be("old", "a fill is quiet");

        Sans(await writer.SetIfChangedAsync(key, "first", StringComparer.Ordinal, TimeSpan.FromHours(1), Ct)).Should().Be(new ChangeResult<string>(ChangeOutcome.Unchanged, "first"));
        (await Raw.KeyTimeToLiveAsync(redisKey))!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(60), "an unchanged value does not refresh the lifetime");

        Sans(await writer.SetIfChangedAsync(key, "second", StringComparer.Ordinal, Ttl, Ct)).Should().Be(new ChangeResult<string>(ChangeOutcome.Replaced, "second"));
        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "second")).Should().BeTrue("a replacement is announced as a write is");
        (await Raw.KeyTimeToLiveAsync(redisKey))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(1), "a replacement carries the new lifetime");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_compare_and_set_that_lost_reads_and_compares_again(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node("a", scripts);
        var key = Unique();
        (await node.SetAsync(key, "base", Ct)).Should().BeTrue();
        var redisKey = await FindAsync(key);
        var calls = 0;
        var comparer = new DelegateComparer(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Raw.StringSet(redisKey, "\"racer\"", Ttl);
            }

            return false;
        });

        var result = await node.SetIfChangedAsync(key, "mine", comparer, Ttl, Ct);

        result.Should().Be(new ChangeResult<string>(ChangeOutcome.Replaced, "mine"));
        calls.Should().Be(2, "the lost write was read and compared again");
        (await Raw.StringGetAsync(redisKey)).ToString().Should().Contain("mine");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Three_lost_compare_and_sets_fall_back_to_a_plain_write(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node("a", scripts);
        var key = Unique();
        (await node.SetAsync(key, "base", Ct)).Should().BeTrue();
        var redisKey = await FindAsync(key);
        var calls = 0;
        var comparer = new DelegateComparer(() =>
        {
            Raw.StringSet(redisKey, $"\"racer-{Interlocked.Increment(ref calls)}\"", Ttl);
            return false;
        });

        var result = await node.SetIfChangedAsync(key, "mine", comparer, Ttl, Ct);

        result.Should().Be(new ChangeResult<string>(ChangeOutcome.Replaced, "mine"));
        calls.Should().Be(3);
        (await Raw.StringGetAsync(redisKey)).ToString().Should().Contain("mine");
    }

    [Fact]
    public async Task A_snapshot_on_a_layered_cache_reads_the_shared_tier()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node("a");
        var key = Unique();
        (await node.SetAsync(key, "old", Ct)).Should().BeTrue();
        (await node.GetAsync<string>(key, Ct)).Should().Be("old");
        await Raw.StringSetAsync(await FindAsync(key), "\"fresh\"", Ttl);

        var entry = (await node.AsMigrationCache().ReadSnapshotAsync<string>(key, null, Ct)).Entry!;

        entry.Value.Should().Be("fresh");
        entry.Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow + Ttl, TimeSpan.FromSeconds(15));
        (await node.GetAsync<string>(key, Ct)).Should().Be("old");
    }

    [Theory]
    [InlineData(KnownCacheProviderNames.InMemoryRedis)]
    [InlineData(KnownCacheProviderNames.Redis)]
    public async Task The_typed_caches_the_factory_builds_take_the_new_members(string provider)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var typed = Provider("a", false).GetRequiredService<ICacheFactory>().CreateCache<string>(provider);
        var key = Unique();

        (await typed.TryFillAsync(key, "a", Ttl, Ct)).Outcome.Should().Be(FillOutcome.Added);
        Sans(await typed.TryFillAsync(key, "b", Ttl, Ct)).Should().Be(new FillResult<string>(FillOutcome.Existed, "a"));
        (await typed.SetIfChangedAsync(key, "a", StringComparer.Ordinal, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Unchanged);
        (await typed.SetIfChangedAsync(key, "c", StringComparer.Ordinal, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Replaced);
        (await typed.GetAsync(key, Ct)).Should().Be("c");
    }

    [Fact]
    public async Task An_unreachable_server_is_a_failure_and_the_value_is_still_kept_locally()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var provider = new ServiceCollection()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = "127.0.0.1:1,abortConnect=false,connectTimeout=50");
                    b.AddRedis();
                    b.AddInMemoryRedis(o => o.LocalMaxExpirationDisconnected = TimeSpan.FromMinutes(1));
                },
                o => o.AppShortName = _app)
            .BuildServiceProvider();
        _nodes.Add(provider);
        var factory = provider.GetRequiredService<ICacheFactory>();
        var redis = factory.CreateCache(KnownCacheProviderNames.Redis);
        var layered = factory.CreateCache(KnownCacheProviderNames.InMemoryRedis);

        (await redis.TryFillAsync("k", "v", Ttl, Ct)).Outcome.Should().Be(FillOutcome.Failed);
        (await redis.SetIfChangedAsync("k", "v", StringComparer.Ordinal, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Failed);
        (await layered.TryFillAsync("k", "v", Ttl, Ct)).Outcome.Should().Be(FillOutcome.Failed);
        (await layered.GetAsync<string>("k", Ct)).Should().Be("v");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_fill_that_added_does_not_keep_its_value_after_a_peer_event_that_came_during_the_call(bool scripts, bool pubsub)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", scripts, pubsub);
        var peer = Node("b", scripts, pubsub);
        await WarmAsync(writer, peer);
        var key = Unique();
        await ArmAsync(writer, peer);
        var redisKey = await EnsureKeyAsync(peer, key);
        await SettledAsync(writer, peer);
        var value = new Probe { Name = "mine", OnSerialize = () => PeerWritesAndWriterHears(writer, peer, key, remove: true) };

        (await writer.TryFillAsync(key, value, Ttl, Ct)).Outcome.Should().Be(FillOutcome.Added);
        await Raw.StringSetAsync(redisKey, "\"other\"", Ttl);

        (await writer.GetAsync<Probe>(key, Ct))!.Name.Should().Be("other", "a value kept locally would still read \"mine\"");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fill_that_failed_does_not_keep_its_value_after_a_peer_event_that_came_during_the_call(bool pubsub)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", pubsub: pubsub);
        var peer = Node("b", pubsub: pubsub);
        await WarmAsync(writer, peer);
        var key = Unique();
        await ArmAsync(writer, peer);
        var value = new Probe
        {
            Name = "mine",
            OnSerialize = () =>
            {
                PeerWritesAndWriterHears(writer, peer, key, remove: true);
                throw new InvalidOperationException("The value cannot be serialized.");
            },
        };

        (await writer.TryFillAsync(key, value, Ttl, Ct)).Outcome.Should().Be(FillOutcome.Failed);

        (await writer.GetAsync<Probe>(key, Ct)).Should().BeNull("the value was not kept over the invalidation that came during the call");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_replacement_does_not_keep_its_value_after_a_peer_event_that_came_during_the_call(bool pubsub)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Node("a", pubsub: pubsub);
        var peer = Node("b", pubsub: pubsub);
        await WarmAsync(writer, peer);
        var key = Unique();
        (await peer.SetAsync(key, "base", Ct)).Should().BeTrue();
        var redisKey = await FindAsync(key);
        await SettledAsync(writer, peer);
        await ArmAsync(writer, peer);
        var racing = new DelegateComparer(() =>
        {
            PeerWritesAndWriterHears(writer, peer, key, rewrite: "base");
            return false;
        });

        Sans(await writer.SetIfChangedAsync(key, "mine", racing, Ttl, Ct)).Should().Be(new ChangeResult<string>(ChangeOutcome.Replaced, "mine"));
        await Raw.StringSetAsync(redisKey, "\"other\"", Ttl);

        (await writer.GetAsync<string>(key, Ct)).Should().Be("other", "a value kept locally would still read \"mine\"");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_deadline_stays_absolute_while_a_slow_comparer_runs(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var clock = new SkewedClock();
        var node = RedisOnly("a", scripts, clock);
        var key = Unique();
        (await node.SetAsync(key, "base", TimeSpan.FromHours(1), Ct)).Should().BeTrue();
        var deadline = clock.GetUtcNow().AddMinutes(10);
        var slow = new DelegateComparer(() =>
        {
            clock.Skew += TimeSpan.FromMinutes(4);
            return false;
        });

        Sans(await node.SetIfChangedAsync(key, "mine", slow, deadline, Ct)).Should().Be(new ChangeResult<string>(ChangeOutcome.Replaced, "mine"));

        (await Raw.KeyTimeToLiveAsync(await FindAsync(key)))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(5)).And.BeLessThan(TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(15), "the 4 minutes the comparer took are not given back");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_deadline_that_passes_during_the_call_writes_nothing_and_fails(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var clock = new SkewedClock();
        var node = RedisOnly("a", scripts, clock);
        var key = Unique();
        (await node.SetAsync(key, "base", TimeSpan.FromHours(1), Ct)).Should().BeTrue();
        var deadline = clock.GetUtcNow().AddMinutes(10);
        var slow = new DelegateComparer(() =>
        {
            clock.Skew += TimeSpan.FromMinutes(11);
            return false;
        });

        (await node.SetIfChangedAsync(key, "mine", slow, deadline, Ct)).Outcome.Should().Be(ChangeOutcome.Failed);

        (await Raw.StringGetAsync(await FindAsync(key))).ToString().Should().Contain("base");
    }

    [Fact]
    public async Task A_fill_whose_script_was_refused_works_the_lifetime_out_again_before_the_fallback_write()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.SupportsAcl, "Needs ACL SETUSER, which this server does not allow.");
        var clock = new SkewedClock();
        var key = Unique();
        await WithoutScriptsAsync(async logs =>
        {
            logs.OnRefused = () => clock.Skew += TimeSpan.FromMinutes(11);
            var node = RedisOnly("a", true, clock, logs: logs);

            (await node.TryFillAsync(key, "mine", clock.GetUtcNow().AddMinutes(10), Ct)).Outcome.Should().Be(FillOutcome.Failed);
        });

        (await Raw.KeyExistsAsync(await FindOrNullAsync(key) ?? "none")).Should().BeFalse("the deadline had passed when the fallback write was due");
    }

    [Fact]
    public async Task A_replacement_whose_script_was_refused_works_the_lifetime_out_again_before_the_fallback_transaction()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.SupportsAcl, "Needs ACL SETUSER, which this server does not allow.");
        var clock = new SkewedClock();
        var key = Unique();
        await WithoutScriptsAsync(async logs =>
        {
            var node = RedisOnly("a", true, clock, logs: logs);
            (await node.SetAsync(key, "base", TimeSpan.FromHours(1), Ct)).Should().BeTrue();
            logs.OnRefused = () => clock.Skew += TimeSpan.FromMinutes(11);

            (await node.SetIfChangedAsync(key, "mine", StringComparer.Ordinal, clock.GetUtcNow().AddMinutes(10), Ct)).Outcome.Should().Be(ChangeOutcome.Failed);
        });

        (await Raw.StringGetAsync(await FindAsync(key))).ToString().Should().Contain("base");
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_comparer_that_throws_reaches_the_caller_and_writes_nothing(bool scripts, bool layered)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = layered ? Node("a", scripts) : RedisOnly("a", scripts);
        var key = Unique();
        (await node.SetAsync(key, "base", Ct)).Should().BeTrue();
        var redisKey = await FindAsync(key);
        var boom = new InvalidOperationException("The comparer failed.");
        var throwing = new DelegateComparer(() => throw boom);

        var act = async () => await node.SetIfChangedAsync(key, "mine", throwing, Ttl, Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        (await Raw.StringGetAsync(redisKey)).ToString().Should().Contain("base");
        (await node.GetAsync<string>(key, Ct)).Should().Be("base");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_deadline_that_lapses_after_the_last_lost_compare_and_set_writes_nothing_and_fails(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var clock = new LapsingClock();
        var node = RedisOnly("a", scripts, clock);
        var key = Unique();
        (await node.SetAsync(key, "base", TimeSpan.FromHours(1), Ct)).Should().BeTrue();
        var redisKey = await FindAsync(key);
        var deadline = clock.GetUtcNow().AddMinutes(10);
        var calls = 0;
        var comparer = new DelegateComparer(() =>
        {
            var n = Interlocked.Increment(ref calls);
            Raw.StringSet(redisKey, $"\"racer-{n}\"", Ttl);
            if (n == 3)
            {
                // The lost compare-and-set reads the clock twice with scripts, three times without; the fallback write is the next read.
                clock.LapseAfter(scripts ? 2 : 3);
            }

            return false;
        });

        (await node.SetIfChangedAsync(key, "mine", comparer, deadline, Ct)).Outcome.Should().Be(ChangeOutcome.Failed);

        calls.Should().Be(3);
        (await Raw.StringGetAsync(redisKey)).ToString().Should().Contain("racer-3");
    }

    [Fact]
    public async Task A_standalone_redis_cache_rejects_a_lifetime_that_is_not_positive_as_the_interface_documents()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = RedisOnly("a", false);
        var key = Unique();
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);

        foreach (var act in new Func<Task>[]
        {
            async () => await node.TryFillAsync(key, "v", TimeSpan.Zero, Ct),
            async () => await node.TryFillAsync(key, "v", past, Ct),
            async () => await node.SetIfChangedAsync(key, "v", StringComparer.Ordinal, TimeSpan.Zero, Ct),
            async () => await node.SetIfChangedAsync(key, "v", StringComparer.Ordinal, past, Ct),
        })
        {
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fill_and_a_replacement_with_no_deadline_leave_no_expiry(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = RedisOnly("a", scripts);
        var (filled, replaced) = (Unique(), Unique());

        (await node.TryFillAsync(filled, "v", DateTimeOffset.MaxValue, Ct)).Outcome.Should().Be(FillOutcome.Added);
        (await node.SetAsync(replaced, "old", Ttl, Ct)).Should().BeTrue();
        (await node.SetIfChangedAsync(replaced, "new", StringComparer.Ordinal, DateTimeOffset.MaxValue, Ct)).Outcome.Should().Be(ChangeOutcome.Replaced);

        (await Raw.KeyTimeToLiveAsync(await FindAsync(filled))).Should().BeNull();
        (await Raw.KeyTimeToLiveAsync(await FindAsync(replaced))).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_results_carry_the_expiry_of_the_stored_entry(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = RedisOnly("a", scripts);
        var (filled, changed, forever) = (Unique(), Unique(), Unique());
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        var later = DateTimeOffset.UtcNow.AddHours(2);
        var tolerance = TimeSpan.FromSeconds(15);

        var added = await node.TryFillAsync(filled, "v", deadline, Ct);
        var existed = await node.TryFillAsync(filled, "w", later, Ct);
        var foreverAdded = await node.TryFillAsync(forever, "v", DateTimeOffset.MaxValue, Ct);
        var foreverExisted = await node.TryFillAsync(forever, "w", deadline, Ct);
        var firstFilled = await node.SetIfChangedAsync(changed, "v", StringComparer.Ordinal, deadline, Ct);
        var unchanged = await node.SetIfChangedAsync(changed, "v", StringComparer.Ordinal, later, Ct);
        var replaced = await node.SetIfChangedAsync(changed, "x", StringComparer.Ordinal, later, Ct);

        added.Outcome.Should().Be(FillOutcome.Added);
        added.Expiration.Should().BeCloseTo(deadline, tolerance);
        existed.Outcome.Should().Be(FillOutcome.Existed);
        existed.Expiration.Should().BeCloseTo(deadline, tolerance, "the stored entry keeps its own lifetime");
        foreverAdded.Expiration.Should().Be(DateTimeOffset.MaxValue);
        foreverExisted.Expiration.Should().Be(DateTimeOffset.MaxValue);
        firstFilled.Outcome.Should().Be(ChangeOutcome.Filled);
        firstFilled.Expiration.Should().BeCloseTo(deadline, tolerance);
        unchanged.Outcome.Should().Be(ChangeOutcome.Unchanged);
        unchanged.Expiration.Should().BeCloseTo(deadline, tolerance, "an unchanged value keeps its lifetime");
        replaced.Outcome.Should().Be(ChangeOutcome.Replaced);
        replaced.Expiration.Should().BeCloseTo(later, tolerance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fill_over_a_value_nothing_reads_answers_unreadable_and_a_compare_and_set_replaces_it(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = RedisOnly("a", scripts);
        var key = Unique();
        (await node.SetAsync(key, "base", Ct)).Should().BeTrue();
        var redisKey = await FindAsync(key);
        await Raw.StringSetAsync(redisKey, "not json{", Ttl);

        Sans(await node.TryFillAsync(key, "mine", Ttl, Ct)).Should().Be(new FillResult<string>(FillOutcome.Unreadable, null));

        (await Raw.StringGetAsync(redisKey)).ToString().Should().Be("not json{");
        var healed = await node.SetIfChangedAsync(key, "healed", StringComparer.Ordinal, Ttl, Ct);
        healed.Outcome.Should().Be(ChangeOutcome.Replaced);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Null_values_are_filled_found_and_compared_without_the_comparer_when_nulls_are_cached(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = RedisOnly("a", scripts, nulls: true);
        var key = Unique();
        var comparer = new StrictComparer();

        Sans(await node.TryFillAsync<string>(key, null, Ttl, Ct)).Should().Be(new FillResult<string>(FillOutcome.Added, null));
        Sans(await node.TryFillAsync(key, "x", Ttl, Ct)).Should().Be(new FillResult<string>(FillOutcome.Existed, null));
        (await node.SetIfChangedAsync<string>(key, null, comparer, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Unchanged);
        (await node.SetIfChangedAsync(key, "x", comparer, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Replaced);
        (await node.SetIfChangedAsync<string>(key, null, comparer, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Replaced);
        (await node.GetAsync<string>(key, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_null_is_a_failure_that_stores_nothing_when_nulls_are_not_cached()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = RedisOnly("a", false);
        var key = Unique();

        (await node.TryFillAsync<string>(key, null, Ttl, Ct)).Outcome.Should().Be(FillOutcome.Failed);
        (await node.SetIfChangedAsync<string>(key, null, StringComparer.Ordinal, Ttl, Ct)).Outcome.Should().Be(ChangeOutcome.Failed);

        (await node.ContainsAsync<string>(key, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_fill_over_a_value_that_cannot_be_read_fails_and_leaves_it_and_a_replacement_heals_it()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var layered = Node("a");
        var seed = Node("b");
        var key = Unique();
        (await seed.SetAsync(key, "base", Ct)).Should().BeTrue();
        var redisKey = await FindAsync(key);
        await Raw.StringSetAsync(redisKey, "not json{", Ttl);
        await SettledAsync(layered, seed);

        (await layered.TryFillAsync(key, "mine", Ttl, Ct)).Outcome.Should().Be(FillOutcome.Unreadable);

        (await Raw.StringGetAsync(redisKey)).ToString().Should().Be("not json{");
        await Raw.StringSetAsync(redisKey, "\"fixed\"", Ttl);
        (await layered.GetAsync<string>(key, Ct)).Should().Be("fixed", "the value the fill could not read was not kept locally");

        await Raw.StringSetAsync(redisKey, "not json{", Ttl);
        Sans(await layered.SetIfChangedAsync(key, "healed", StringComparer.Ordinal, Ttl, Ct)).Should().Be(new ChangeResult<string>(ChangeOutcome.Replaced, "healed"));
        (await Raw.StringGetAsync(redisKey)).ToString().Should().Contain("healed");
    }

    [Fact]
    public async Task A_fill_and_a_compare_and_set_work_over_a_compressed_stored_value()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(o => o.FormatNegotiation.Mode = FormatNegotiationMode.Force);
                    b.AddMessagePackSerializer();
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.Policies["mp"] = MigrateOnReadHarness.Policy("messagepack", null, migrate: true);
                })
            .BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var policy = provider.GetRequiredService<ICachePolicyFactory>().Resolve("mp")!;
        var key = Unique();
        var big = new TestDto { Id = 6, Name = new string('z', 5000) };
        (await cache.SetAsync(key, big, policy, Ct)).Should().BeTrue();
        var redisKey = await FindAsync(key);
        var stored = (byte[])(await Raw.StringGetAsync(redisKey))!;
        stored[0].Should().Be(0x03, "above the compression threshold the serializer writes the compressed id");
        var comparer = new TestDtoComparer();

        var filled = await cache.TryFillAsync(key, new TestDto { Id = 1, Name = "other" }, Ttl, policy, Ct);
        var unchanged = await cache.SetIfChangedAsync(key, new TestDto { Id = 6, Name = big.Name }, comparer, Ttl, policy, Ct);
        var replaced = await cache.SetIfChangedAsync(key, new TestDto { Id = 7, Name = "seven" }, comparer, Ttl, policy, Ct);

        filled.Outcome.Should().Be(FillOutcome.Existed);
        filled.Existing.Should().BeEquivalentTo(big);
        unchanged.Outcome.Should().Be(ChangeOutcome.Unchanged);
        replaced.Outcome.Should().Be(ChangeOutcome.Replaced);
        (await cache.GetAsync<TestDto>(key, policy, Ct)).Should().BeEquivalentTo(new TestDto { Id = 7, Name = "seven" });
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

    private static FillResult<T> Sans<T>(FillResult<T> result) => result with { Expiration = null };

    private static ChangeResult<T> Sans<T>(ChangeResult<T> result) => result with { Expiration = null };

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    // A read by the writer subscribes it to the topic, so the peer's events during its next call are heard.
    private static async Task<string> ArmAsync(ICache writer, ICache peer)
    {
        var probe = Unique();
        (await peer.SetAsync(probe, "s0", Ct)).Should().BeTrue();
        (await writer.GetAsync<string>(probe, Ct)).Should().Be("s0");
        return probe;
    }

    private static async Task SettledAsync(ICache writer, ICache peer)
    {
        var probe = await ArmAsync(peer, writer);
        (await writer.SetAsync(probe, "s1", Ct)).Should().BeTrue();
        (await EventuallyAsync(async () => await peer.GetAsync<string>(probe, Ct) == "s1")).Should().BeTrue("the peer has heard everything the writer announced before the probe");
    }

    // The writer's counter for the key's stripe moves when it processes the peer's event for the key, or when it learns it lost events.
    private static void PeerWritesAndWriterHears(ICache writer, ICache peer, string key, bool remove = false, string? rewrite = null)
    {
        var stripe = StripeOf(writer, key);
        var before = ChangesHeard(writer)[stripe];
        if (rewrite is not null)
        {
            peer.SetAsync(key, rewrite, Ct).AsTask().GetAwaiter().GetResult().Should().BeTrue();
        }

        if (remove)
        {
            peer.SetAsync(key, "transient", Ct).AsTask().GetAwaiter().GetResult().Should().BeTrue();
            peer.RemoveAsync<string>(key, Ct).AsTask().GetAwaiter().GetResult();
        }

        EventuallyAsync(() => Task.FromResult(ChangesHeard(writer)[stripe] > before)).GetAwaiter().GetResult().Should().BeTrue("the writer has processed the peer's event for the key");
    }

    private static int StripeOf(ICache cache, string key)
    {
        var builder = (CacheEntryBuilder)typeof(MultilayerCache).GetField("_entryBuilder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        return BackgroundWrites.Stripe(builder.BuildEntryOptions<string>(key).CacheKey.Name);
    }

    private static long[] ChangesHeard(ICache cache)
    {
        var background = typeof(MultilayerCacheBase).GetField("_background", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        return (long[])typeof(BackgroundWrites).GetField("_changes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(background)!;
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

        return false;
    }

    private static async Task WarmAsync(ICache writer, ICache peer)
    {
        var key = Unique();
        (await peer.SetAsync(key, "old", Ct)).Should().BeTrue();
        (await peer.GetAsync<string>(key, Ct)).Should().Be("old");
        (await writer.SetAsync(key, "new", Ct)).Should().BeTrue();
        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "new")).Should().BeTrue("the peer's subscription to the topic is live");
    }

    private async Task<RedisKey> EnsureKeyAsync(ICache writer, string key)
    {
        (await writer.SetAsync(key, "placeholder", Ct)).Should().BeTrue();
        var redisKey = await FindAsync(key);
        await Raw.KeyDeleteAsync(redisKey);
        return redisKey;
    }


    private async Task WithoutScriptsAsync(Func<RefusalLogs, Task> body)
    {
        await using var admin = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var server = admin.GetServer(admin.GetEndPoints()[0]);
        var user = $"it-fill-{Guid.NewGuid():N}";
        await server.ExecuteAsync("ACL", "SETUSER", user, "on", ">pw", "~*", "&*", "+@all", "-@scripting");
        RestrictedConnection = $"{fixture.ConnectionString},user={user},password=pw";
        try
        {
            await body(new RefusalLogs());
        }
        finally
        {
            RestrictedConnection = null;
            await server.ExecuteAsync("ACL", "DELUSER", user);
        }
    }

    private async Task<RedisKey?> FindOrNullAsync(string key)
    {
        _ = Raw;
        var server = _raw!.GetServer(_raw.GetEndPoints()[0]);
        await Task.CompletedTask;
        return server.Keys(pattern: $"*{key}*").Cast<RedisKey?>().FirstOrDefault();
    }

    private Task<RedisKey> FindAsync(string key)
    {
        _ = Raw;
        var server = _raw!.GetServer(_raw.GetEndPoints()[0]);
        return Task.FromResult(server.Keys(pattern: $"*{key}*").Single());
    }

    private ICache Node(string source, bool scripts = false, bool pubsub = false) =>
        Provider(source, scripts, pubsub: pubsub).GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemoryRedis);

    private ICache RedisOnly(string source, bool scripts, TimeProvider? clock = null, bool nulls = false, ILoggerProvider? logs = null) =>
        Provider(source, scripts, clock, nulls, logs: logs, restricted: logs is not null).GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);

    private ServiceProvider Provider(string source, bool scripts, TimeProvider? clock = null, bool nulls = false, bool pubsub = false, ILoggerProvider? logs = null, bool restricted = false)
    {
        var services = new ServiceCollection();
        if (logs is not null)
        {
            services.AddLogging(l => l.AddProvider(logs));
        }

        if (clock is not null)
        {
            services.AddSingleton(clock);
        }

        var provider = services
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = restricted ? RestrictedConnection ?? fixture.ConnectionString : fixture.ConnectionString);
                    b.AddRedis(o =>
                    {
                        o.ReadScriptsEnabled = scripts;
                        o.CacheNullValues = nulls;
                    });
                    if (pubsub)
                    {
                        b.AddRedisPubSub(o => o.Enabled = true);
                    }

                    b.AddBroadcast();
                    b.AddInMemoryRedis(o =>
                    {
                        o.LocalMaxExpiration = TimeSpan.FromMinutes(1);
                        o.CacheNullValues = nulls;
                    });
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.SourceUri = new Uri($"urn:{source}-{_app}");
                    if (pubsub)
                    {
                        o.DefaultTopic = KnownTopicNames.RedisPubSub;
                    }
                })
            .BuildServiceProvider();
        _nodes.Add(provider);
        return provider;
    }

        private sealed class RefusalLogs : ILoggerProvider
    {
        public Action? OnRefused { get; set; }

        public ILogger CreateLogger(string categoryName) => new Sink(this);

        public void Dispose()
        {
        }

        private sealed class Sink(RefusalLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (formatter(state, exception).Contains("refused the script", StringComparison.Ordinal))
                {
                    owner.OnRefused?.Invoke();
                }
            }
        }
    }

    private sealed class DelegateComparer(Func<bool> equal) : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => equal();

        public int GetHashCode(string obj) => obj.GetHashCode(StringComparison.Ordinal);
    }

    private sealed class StrictComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => string.Equals(x ?? throw new InvalidOperationException("The comparer was asked about a null."), y ?? throw new InvalidOperationException("The comparer was asked about a null."), StringComparison.Ordinal);

        public int GetHashCode(string obj) => obj.GetHashCode(StringComparison.Ordinal);
    }

    private sealed class TestDtoComparer : IEqualityComparer<TestDto>
    {
        public bool Equals(TestDto? x, TestDto? y) => x is not null && y is not null && x.Id == y.Id && x.Name == y.Name;

        public int GetHashCode(TestDto obj) => obj.Id;
    }

    /// <summary>The system clock until a set number of reads has passed, and eleven minutes ahead of it after.</summary>
    private sealed class LapsingClock : TimeProvider
    {
        private int _reads = int.MaxValue;

        public void LapseAfter(int reads) => Volatile.Write(ref _reads, reads);

        public override DateTimeOffset GetUtcNow() => Interlocked.Decrement(ref _reads) < 0 ? base.GetUtcNow().AddMinutes(11) : base.GetUtcNow();
    }

    /// <summary>The system clock plus an offset a test moves.</summary>
    private sealed class SkewedClock : TimeProvider
    {
        private long _skewTicks;

        public TimeSpan Skew
        {
            get => TimeSpan.FromTicks(Interlocked.Read(ref _skewTicks));
            set => Interlocked.Exchange(ref _skewTicks, value.Ticks);
        }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Skew;
    }

    /// <summary>A value that runs a callback while it is serialized.</summary>
    [JsonConverter(typeof(ProbeConverter))]
    private sealed class Probe
    {
        public string? Name { get; init; }

        public Action? OnSerialize { get; init; }
    }

    private sealed class ProbeConverter : JsonConverter<Probe>
    {
        public override Probe Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new() { Name = reader.GetString() };

        public override void Write(Utf8JsonWriter writer, Probe value, JsonSerializerOptions options)
        {
            value.OnSerialize?.Invoke();
            writer.WriteStringValue(value.Name);
        }
    }
}
