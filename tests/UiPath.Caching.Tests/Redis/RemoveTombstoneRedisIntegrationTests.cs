using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using UiPath.Caching.Broadcast.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Redis;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Redis;

/// <summary>A remove that leaves a tombstone, on a real Redis: every read treats it as a miss, a fill does not write over one younger than its generator, and it expires.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class RemoveTombstoneRedisIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private readonly string _app = $"tb{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];
    private ManualTimeProvider? _time;
    private ConnectionMultiplexer? _raw;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ManualTimeProvider Clock => _time ??= new(new DateTimeOffset(Server().Time(), TimeSpan.Zero));

    private IDatabase Raw => (_raw ??= ConnectionMultiplexer.Connect(fixture.ConnectionString)).GetDatabase();

    [Fact]
    public async Task A_remove_leaves_a_tombstone_in_place_of_the_key_and_it_expires()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = (await Node("a", scripts: false, TimeSpan.FromMilliseconds(700))).Cache;
        var key = $"it-{Guid.NewGuid():N}";
        await cache.SetAsync(key, "v", Ttl, Ct);

        (await cache.RemoveAsync<string>(key, Ct)).Should().BeTrue();

        var redisKey = Find(key);
        var held = await Raw.StringGetAsync(redisKey);
        TombstoneValue.TryRead(held, out var grace).Should().BeTrue("the key holds a tombstone, not the value and not nothing");
        grace.Should().Be(700);
        ((byte[])held!)[0].Should().Be(0x0F);
        (await Raw.KeyTimeToLiveAsync(redisKey))!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(700));

        await Task.Delay(1200, Ct);

        (await Raw.KeyExistsAsync(redisKey)).Should().BeFalse("the tombstone expired");
    }

    [Fact]
    public async Task Without_the_option_a_remove_deletes_the_key()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = (await Node("a", scripts: false, grace: null)).Cache;
        var key = $"it-{Guid.NewGuid():N}";
        await cache.SetAsync(key, "v", Ttl, Ct);
        var redisKey = Find(key);

        await cache.RemoveAsync<string>(key, Ct);

        (await Raw.KeyExistsAsync(redisKey)).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_tombstone_reads_as_a_miss_on_every_read_path(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = (await Node("a", scripts, Grace, o => o.AwaitRefresh = true)).Cache;
        CacheKey one = $"it-{Guid.NewGuid():N}";
        CacheKey two = $"it-{Guid.NewGuid():N}";
        await cache.SetAsync(one, "v1", Ttl, Ct);
        await cache.SetAsync(two, "v2", Ttl, Ct);
        await cache.RemoveAsync<string>(one, Ct);
        await cache.RemoveAsync<string>([two], Ct);
        TombstoneValue.Is(await Raw.StringGetAsync(Find(one.Name))).Should().BeTrue();
        TombstoneValue.Is(await Raw.StringGetAsync(Find(two.Name))).Should().BeTrue("a remove of many leaves one tombstone per key");

        (await cache.GetAsync<string>(one, Ct)).Should().BeNull("GetAsync");
        (await cache.GetAsync<string>([one, two], Ct)).Select(p => p.Value).Should().AllBeEquivalentTo((string?)null, "GetAsync of many");
        (await cache.GetCacheEntryAsync<string>(one, Ct)).Found.Should().BeFalse("GetCacheEntryAsync");
        (await cache.GetCacheEntriesAsync<string>([one, two], Ct)).Select(p => p.Value.Found).Should().AllBeEquivalentTo(false, "GetCacheEntriesAsync");
        (await cache.AsMigrationCache().ReadSnapshotAsync<string>(one, null, Ct)).Outcome.Should().Be(SnapshotOutcome.Absent, "ReadSnapshotAsync, the read an adoption makes");
        (await cache.ContainsAsync<string>(one, Ct)).Should().BeFalse("ContainsAsync");
        (await cache.TimeToLiveAsync<string>(one, Ct)).Should().BeNull("TimeToLiveAsync");
        (await cache.ExpireTimeAsync<string>(one, Ct)).Should().BeNull("ExpireTimeAsync");
        (await cache.RefreshAsync<string>(one, TimeSpan.FromHours(1), null, Ct)).Should().BeFalse("RefreshAsync");
        (await Raw.KeyTimeToLiveAsync(Find(one.Name)))!.Value.Should().BeLessThanOrEqualTo(Grace, "a refresh does not extend a tombstone");

        (await cache.TryAddAsync(one, "again", Ttl, Ct)).Should().BeTrue("TryAddAsync treats a removed key as absent");
        (await cache.GetAsync<string>(one, Ct)).Should().Be("again");
        (await cache.TryAddAsync(one, "third", Ttl, Ct)).Should().BeFalse("and a held key as held");
    }

    [Fact]
    public async Task A_set_of_a_default_value_is_a_remove()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = (await Node("a", scripts: false, Grace)).Cache;
        CacheKey single = $"it-{Guid.NewGuid():N}";
        CacheKey many = $"it-{Guid.NewGuid():N}";
        await cache.SetAsync(single, "v", Ttl, Ct);
        await cache.SetAsync(many, "v", Ttl, Ct);

        await cache.SetAsync<string>(single, null, Ttl, Ct);
        await cache.SetAsync<string>([new(many, null)], Ttl, Ct);

        TombstoneValue.Is(await Raw.StringGetAsync(Find(single.Name))).Should().BeTrue();
        TombstoneValue.Is(await Raw.StringGetAsync(Find(many.Name))).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fill_after_a_concurrent_remove_does_not_resurrect_the_key(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var a = (await Node("a", scripts, Grace)).Layered;
        var b = (await Node("b", scripts, Grace)).Layered;
        var key = $"it-{Guid.NewGuid():N}";
        var (generated, release) = Gate();
        async Task<string?> Generate(CancellationToken _)
        {
            generated.SetResult();
            await release.Task;
            return "from-a";
        }

        var slow = a.GetOrAddAsync<string>(key, Generate, Ttl, Ct).AsTask();
        await generated.Task.WaitAsync(Ct);

        Clock.Advance(Grace);
        await b.RemoveAsync<string>(key, Ct);
        release.SetResult();

        (await slow).Should().Be("from-a", "the caller gets what its generator made");
        TombstoneValue.Is(await Raw.StringGetAsync(Find(key))).Should().BeTrue("the fill left the tombstone alone");
        (await a.GetAsync<string>(key, Ct)).Should().BeNull("and this node kept nothing");
        (await b.GetAsync<string>(key, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Without_the_option_the_same_fill_puts_the_value_back()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var a = (await Node("a", scripts: false, grace: null)).Layered;
        var b = (await Node("b", scripts: false, grace: null));
        var key = $"it-{Guid.NewGuid():N}";
        var (generated, release) = Gate();
        async Task<string?> Generate(CancellationToken _)
        {
            generated.SetResult();
            await release.Task;
            return "from-a";
        }

        var slow = a.GetOrAddAsync<string>(key, Generate, Ttl, Ct).AsTask();
        await generated.Task.WaitAsync(Ct);

        // Through the Redis-only cache, which tells no peer: a layered remove would also reach the fill's watch, first or not depending on timing.
        await b.Cache.RemoveAsync<string>(key, Ct);
        release.SetResult();
        await slow;

        (await b.Layered.GetAsync<string>(key, Ct)).Should().Be("from-a", "with no tombstone nothing tells the fill that the key was removed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_batch_fill_after_a_concurrent_remove_leaves_that_key_alone(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var a = (await Node("a", scripts, Grace)).Layered;
        var b = (await Node("b", scripts, Grace)).Layered;
        var (x, y) = ($"it-{Guid.NewGuid():N}", $"it-{Guid.NewGuid():N}");
        var (generated, release) = Gate();
        async Task<KeyValuePair<int, string?>[]> Generate(int[] states, CancellationToken _)
        {
            generated.TrySetResult();
            await release.Task;
            return [.. states.Select(s => new KeyValuePair<int, string?>(s, $"from-a-{s}"))];
        }

        var batch = a.GetOrAddAsync<string, int>([new(x, 1), new(y, 2)], Generate, Ttl, null, Ct).AsTask();
        await generated.Task.WaitAsync(Ct);

        Clock.Advance(Grace);
        await b.RemoveAsync<string>(y, Ct);
        release.SetResult();

        (await batch).Select(r => r.Value).Should().BeEquivalentTo("from-a-1", "from-a-2");
        (await Raw.StringGetAsync(Find(x))).ToString().Should().Contain("from-a-1");
        TombstoneValue.Is(await Raw.StringGetAsync(Find(y))).Should().BeTrue("only the key that was removed is left alone");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fill_whose_generator_started_after_the_remove_replaces_the_tombstone(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var a = (await Node("a", scripts, Grace)).Layered;
        var b = (await Node("b", scripts, Grace)).Layered;
        var key = $"it-{Guid.NewGuid():N}";
        await b.SetAsync(key, "old", Ttl, Ct);
        await b.RemoveAsync<string>(key, Ct);
        await AgeAsync(key, TimeSpan.FromMilliseconds(400));

        (await a.GetOrAddAsync<string>(key, _ => Task.FromResult<string?>("new"), Ttl, Ct)).Should().Be("new");

        TombstoneValue.Is(await Raw.StringGetAsync(Find(key))).Should().BeFalse();
        (await b.GetAsync<string>(key, Ct)).Should().Be("new", "the removal is older than the generator, so the value it made is the key's");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_fill_refuses_a_tombstone_younger_than_its_generator_and_replaces_an_older_one(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var fenced = (IFencedFillTier)(await Node("a", scripts, Grace)).Provider.CreateCache();
        var (young, old, plain, held) = ($"it-{Guid.NewGuid():N}", $"it-{Guid.NewGuid():N}", $"it-{Guid.NewGuid():N}", $"it-{Guid.NewGuid():N}");
        var cache = (ICache)fenced;
        foreach (var key in new[] { young, old, plain })
        {
            await cache.SetAsync(key, "old", Ttl, Ct);
            await cache.RemoveAsync<string>(key, Ct);
        }

        await cache.SetAsync(held, "stored", Ttl, Ct);
        foreach (var key in new[] { young, old, plain })
        {
            await AgeAsync(key, TimeSpan.FromMilliseconds(400));
        }

        var refused = await fenced.TryFillAfterAsync<string>(young, "v", Clock.ToDateTimeOffset(Ttl), TimeSpan.FromSeconds(10), null, Ct);
        var replaced = await fenced.TryFillAfterAsync<string>(old, "v", Clock.ToDateTimeOffset(Ttl), TimeSpan.FromMilliseconds(20), null, Ct);
        var unfenced = await cache.TryFillAsync(plain, "v", Ttl, Ct);
        var existed = await fenced.TryFillAfterAsync<string>(held, "v", Clock.ToDateTimeOffset(Ttl), TimeSpan.FromSeconds(10), null, Ct);

        refused.Outcome.Should().Be(FillOutcome.Removed, "the generator started 10 s ago and the key was removed 0.4 s ago");
        TombstoneValue.Is(await Raw.StringGetAsync(Find(young))).Should().BeTrue("a refused fill leaves the tombstone");
        replaced.Outcome.Should().Be(FillOutcome.Added, "the generator started 20 ms ago, after the removal");
        (await Raw.StringGetAsync(Find(old))).ToString().Should().Contain("v");
        unfenced.Outcome.Should().Be(FillOutcome.Added, "a fill with no generator has nothing to compare, so a removed key is absent");
        existed.Outcome.Should().Be(FillOutcome.Existed);
        existed.Existing.Should().Be("stored");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_batch_fill_refuses_the_tombstoned_key_and_adds_the_absent_one(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = (await Node("a", scripts, Grace));
        var many = (IFillManyTier)node.Provider.CreateCache();
        var cache = (ICache)many;
        var (removed, absent) = ($"it-{Guid.NewGuid():N}", $"it-{Guid.NewGuid():N}");
        await cache.SetAsync(removed, "old", Ttl, Ct);
        await cache.RemoveAsync<string>(removed, Ct);
        await AgeAsync(removed, TimeSpan.FromMilliseconds(300));

        var results = await many.TryFillManyAsync<string>([new(removed, "v"), new(absent, "v")], Clock.ToDateTimeOffset(Ttl), null, Ct, TimeSpan.FromSeconds(10));

        results.Select(r => r.Outcome).Should().Equal(FillOutcome.Removed, FillOutcome.Added);
        TombstoneValue.Is(await Raw.StringGetAsync(Find(removed))).Should().BeTrue();
    }

    [Fact]
    public async Task SetIfChanged_over_a_tombstone_fills_the_key()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = (await Node("a", scripts: false, Grace)).Cache;
        var key = $"it-{Guid.NewGuid():N}";
        await cache.SetAsync(key, "v", Ttl, Ct);
        await cache.RemoveAsync<string>(key, Ct);

        var changed = await cache.SetIfChangedAsync(key, "new", EqualityComparer<string>.Default, Ttl, Ct);

        changed.Outcome.Should().Be(ChangeOutcome.Filled, "a removed key is absent");
        (await cache.GetAsync<string>(key, Ct)).Should().Be("new");
    }

    [Fact]
    public async Task A_node_that_sets_the_option_does_not_count_a_tombstone_as_unreadable_or_delete_it()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var on = (await Node("a", scripts: false, Grace, serializers: true));
        var key = $"it-{Guid.NewGuid():N}";
        await on.Cache.SetAsync(key, "v", Ttl, Ct);
        await on.Cache.RemoveAsync<string>(key, Ct);

        (await on.Cache.GetAsync<string>(key, Ct)).Should().BeNull();
        (await on.Cache.GetCacheEntryAsync<string>(key, Ct)).Found.Should().BeFalse();

        TombstoneValue.Is(await Raw.StringGetAsync(Find(key))).Should().BeTrue("RemoveUnreadable is on and a format is registered, yet the tombstone is no unreadable value");
        on.Unreadable().Should().BeEmpty();
    }

    [Fact]
    public async Task A_node_without_the_option_reads_a_tombstone_as_a_miss_and_leaves_it()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var on = (await Node("a", scripts: false, Grace, serializers: true));
        var off = (await Node("b", scripts: false, grace: null, serializers: true));
        var plain = (await Node("c", scripts: false, grace: null));
        var key = $"it-{Guid.NewGuid():N}";
        await on.Cache.SetAsync(key, "v", Ttl, Ct);
        await on.Cache.RemoveAsync<string>(key, Ct);

        (await off.Cache.GetAsync<string>(key, Ct)).Should().BeNull("a miss either way");
        (await off.Cache.GetCacheEntryAsync<string>(key, Ct)).Found.Should().BeFalse();
        (await plain.Cache.GetAsync<string>(key, Ct)).Should().BeNull("a node with no serializers registered reads it as a miss as well");

        TombstoneValue.Is(await Raw.StringGetAsync(Find(key))).Should().BeTrue("the tombstone is not deleted as an unreadable value, so the fence stands");
        off.Unreadable().Should().BeEmpty("and it is not counted as one");
        plain.Unreadable().Should().BeEmpty();
    }

    [Fact]
    public async Task A_node_that_reads_values_without_a_header_reads_a_tombstone_as_a_miss_without_a_failure()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var logs = new RecordedLogger();
        var on = (await Node("a", scripts: false, Grace));
        var plain = (await Node("b", scripts: false, grace: null, logs: logs));
        var key = $"it-{Guid.NewGuid():N}";
        await on.Cache.SetAsync(key, "v", Ttl, Ct);
        await on.Cache.RemoveAsync<string>(key, Ct);

        (await plain.Cache.GetAsync<string>(key, Ct)).Should().BeNull();
        (await plain.Cache.GetCacheEntryAsync<string>(key, Ct)).Found.Should().BeFalse();

        logs.Records.Where(r => r.Level >= LogLevel.Warning).Should().BeEmpty("the deserializer is not asked to read a tombstone");
        TombstoneValue.Is(await Raw.StringGetAsync(Find(key))).Should().BeTrue();
    }

    [Fact]
    public async Task A_tombstone_stays_while_RemoveUnreadable_is_off()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var on = (await Node("a", scripts: false, Grace, serializers: true));
        var off = (await Node("b", scripts: false, grace: null, o => o.RemoveUnreadable = false, serializers: true));
        var key = $"it-{Guid.NewGuid():N}";
        await on.Cache.SetAsync(key, "v", Ttl, Ct);
        await on.Cache.RemoveAsync<string>(key, Ct);

        (await off.Cache.GetAsync<string>(key, Ct)).Should().BeNull();

        TombstoneValue.Is(await Raw.StringGetAsync(Find(key))).Should().BeTrue();
    }

    [Fact]
    public async Task Every_remove_leaves_bytes_of_its_own()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = (await Node("a", scripts: false, Grace)).Cache;
        var key = $"it-{Guid.NewGuid():N}";
        await cache.SetAsync(key, "v", Ttl, Ct);

        await cache.RemoveAsync<string>(key, Ct);
        var first = (byte[])(await Raw.StringGetAsync(Find(key)))!;
        await cache.RemoveAsync<string>(key, Ct);
        var second = (byte[])(await Raw.StringGetAsync(Find(key)))!;

        TombstoneValue.Is(first).Should().BeTrue();
        TombstoneValue.Is(second).Should().BeTrue();
        second.Should().NotEqual(first, "a swap that compares the bytes it read must lose to a second removal");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_second_remove_between_the_read_and_the_swap_keeps_the_fence(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var fenced = (RedisCache)(await Node("a", scripts, Grace)).Provider.CreateCache();
        var cache = fenced;
        var key = $"it-{Guid.NewGuid():N}";
        await cache.SetAsync(key, "old", Ttl, Ct);
        await cache.RemoveAsync<string>(key, Ct);
        await AgeAsync(key, TimeSpan.FromMilliseconds(400));
        var swaps = 0;

        // The tombstone is older than the generator, so the fill goes to swap it; a removal lands first.
        fenced.BeforeTombstoneSwap = async () =>
        {
            swaps++;
            fenced.BeforeTombstoneSwap = null;
            Clock.Advance(Grace);
            await cache.RemoveAsync<string>(key, Ct);
        };

        var result = await fenced.TryFillAfterAsync<string>(key, "v", Clock.ToDateTimeOffset(Ttl), TimeSpan.FromMilliseconds(20), null, Ct);

        swaps.Should().Be(1);
        result.Outcome.Should().Be(FillOutcome.Removed, "the new tombstone is younger than the generator");
        TombstoneValue.Is(await Raw.StringGetAsync(Find(key))).Should().BeTrue();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_tombstone_with_no_deadline_or_past_its_grace_refuses_the_fill_and_goes_back_to_its_grace(bool scripts, bool persisted)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var fenced = (IFencedFillTier)(await Node("a", scripts, Grace)).Provider.CreateCache();
        var cache = (ICache)fenced;
        var key = $"it-{Guid.NewGuid():N}";
        await cache.SetAsync(key, "old", Ttl, Ct);
        await cache.RemoveAsync<string>(key, Ct);
        await AgeAsync(key, TimeSpan.FromMilliseconds(400));
        var redisKey = Find(key);
        _ = persisted
            ? await Raw.KeyPersistAsync(redisKey)
            : await Raw.KeyExpireAsync(redisKey, TimeSpan.FromMinutes(10));

        var result = await fenced.TryFillAfterAsync<string>(key, "v", Clock.ToDateTimeOffset(Ttl), TimeSpan.FromMilliseconds(20), null, Ct);

        result.Outcome.Should().Be(FillOutcome.Removed, "its age cannot be told, so it counts as younger than the generator");
        TombstoneValue.Is(await Raw.StringGetAsync(redisKey)).Should().BeTrue();
        (await Raw.KeyTimeToLiveAsync(redisKey)).Should().NotBeNull().And.Subject!.Value.Should().BeLessThanOrEqualTo(Grace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_age_is_judged_when_the_read_is_answered_not_when_the_fill_was_sent(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var clock = Clock;
        var fenced = (RedisCache)(await Node("a", scripts, Grace, clock: clock)).Provider.CreateCache();
        var cache = fenced;
        var key = $"it-{Guid.NewGuid():N}";
        await cache.SetAsync(key, "old", Ttl, Ct);
        await cache.RemoveAsync<string>(key, Ct);
        await AgeAsync(key, TimeSpan.FromMilliseconds(300));

        // The generator had run 30 ms when the fill was made and the removal is 300 ms old, so the removal is the older: the fill would replace it. The time the answer takes, 5 s by the clock the fill sees, passes after the fill has been sent.
        fenced.AfterFillRead = () => clock.Advance(TimeSpan.FromSeconds(5));
        var result = await ((IFencedFillTier)fenced).TryFillAfterAsync<string>(key, "v", clock.ToDateTimeOffset(Ttl), TimeSpan.FromMilliseconds(30), null, Ct);

        result.Outcome.Should().Be(FillOutcome.Removed, "by the time the answer came the generator had run longer than the tombstone had lived");
    }
    [Fact]
    public async Task A_tombstone_without_a_generator_to_compare_with_is_replaced_whatever_its_age()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = (await Node("a", scripts: true, Grace)).Provider.CreateCache();
        var key = $"it-{Guid.NewGuid():N}";
        await cache.SetAsync(key, "old", Ttl, Ct);
        await cache.RemoveAsync<string>(key, Ct);
        _ = await Raw.KeyPersistAsync(Find(key));

        (await cache.TryFillAsync(key, "v", Ttl, Ct)).Outcome.Should().Be(FillOutcome.Added);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_refresh_changes_a_live_key_and_leaves_a_tombstone_alone(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = (await Node("a", scripts, Grace, o => o.AwaitRefresh = true)).Cache;
        var (live, removed) = ($"it-{Guid.NewGuid():N}", $"it-{Guid.NewGuid():N}");
        await cache.SetAsync(live, "v", TimeSpan.FromMinutes(1), Ct);
        await cache.SetAsync(removed, "v", Ttl, Ct);
        await cache.RemoveAsync<string>(removed, Ct);

        (await cache.RefreshAsync<string>(live, TimeSpan.FromHours(1), null, Ct)).Should().BeTrue();
        (await Raw.KeyTimeToLiveAsync(Find(live)))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(30));
        (await cache.RefreshAsync<string>(live, DateTimeOffset.MaxValue, null, Ct)).Should().BeTrue();
        (await Raw.KeyTimeToLiveAsync(Find(live))).Should().BeNull("persisted");
        (await cache.RefreshAsync<string>(removed, DateTimeOffset.MaxValue, null, Ct)).Should().BeFalse();
        (await cache.RefreshAsync<string>(removed, TimeSpan.FromHours(1), null, Ct)).Should().BeFalse();

        (await Raw.KeyTimeToLiveAsync(Find(removed)))!.Value.Should().BeLessThanOrEqualTo(Grace, "a tombstone is neither persisted nor extended");
    }

    [Theory]
    [InlineData("persist", true)]
    [InlineData("shorter", true)]
    [InlineData("longer", true)]
    [InlineData("persist", false)]
    [InlineData("shorter", false)]
    [InlineData("longer", false)]
    public async Task A_removal_between_the_refresh_check_and_its_change_is_not_touched_by_the_change(string change, bool awaited)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var redis = (RedisCache)(await Node("a", scripts: false, Grace, o => o.AwaitRefresh = awaited)).Provider.CreateCache();
        var cache = redis;
        var key = $"it-{Guid.NewGuid():N}";
        await cache.SetAsync(key, "v", TimeSpan.FromMinutes(1), Ct);
        var removals = 0;
        redis.BeforeRefreshChange = async () =>
        {
            removals++;
            redis.BeforeRefreshChange = null;
            await cache.RemoveAsync<string>(key, Ct);
        };

        var refreshed = change switch
        {
            "persist" => await cache.RefreshAsync<string>(key, DateTimeOffset.MaxValue, null, Ct),
            "shorter" => await cache.RefreshAsync<string>(key, TimeSpan.FromSeconds(1), null, Ct),
            _ => await cache.RefreshAsync<string>(key, TimeSpan.FromHours(1), null, Ct),
        };
        // One connection runs its commands in order, so this read answers after the refresh it may not have awaited.
        _ = await cache.GetAsync<string>(key, Ct);

        removals.Should().Be(1);
        refreshed.Should().BeFalse("the key it was to change is a tombstone now");
        TombstoneValue.Is(await Raw.StringGetAsync(Find(key))).Should().BeTrue();
        var left = await Raw.KeyTimeToLiveAsync(Find(key));
        left.Should().NotBeNull("it was not persisted");
        left!.Value.Should().BeLessThanOrEqualTo(Grace, "it was not extended").And.BeGreaterThan(TimeSpan.FromSeconds(20), "it was not shortened, which would let the fence lapse early");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task A_refresh_without_scripts_that_meets_a_tombstone_without_its_grace_puts_it_back(bool awaited, bool persisted)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = (await Node("a", scripts: false, Grace, o => o.AwaitRefresh = awaited)).Cache;
        var key = $"it-{Guid.NewGuid():N}";
        await cache.SetAsync(key, "v", Ttl, Ct);
        await cache.RemoveAsync<string>(key, Ct);
        var redisKey = Find(key);
        _ = persisted ? await Raw.KeyPersistAsync(redisKey) : await Raw.KeyExpireAsync(redisKey, TimeSpan.FromMinutes(10));

        (await cache.RefreshAsync<string>(key, TimeSpan.FromHours(1), null, Ct)).Should().BeFalse();

        (await Raw.KeyTimeToLiveAsync(redisKey)).Should().NotBeNull().And.Subject!.Value.Should().BeLessThanOrEqualTo(Grace, "the default fire-and-forget refresh repairs it as well");
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 34)]
    [InlineData(true, 34)]
    [InlineData(false, 35)]
    [InlineData(true, 35)]
    [InlineData(false, 36)]
    [InlineData(true, 36)]
    [InlineData(false, 5000)]
    [InlineData(true, 5000)]
    public async Task A_refresh_changes_a_live_key_whatever_the_length_of_its_value(bool scripts, int length)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = (await Node("a", scripts, Grace, o => o.AwaitRefresh = true)).Cache;
        var (key, other) = ($"it-{Guid.NewGuid():N}", $"it-{Guid.NewGuid():N}");
        await cache.SetAsync(key, new string('x', length), TimeSpan.FromMinutes(1), Ct);
        await cache.SetAsync(other, new string('x', length), TimeSpan.FromMinutes(1), Ct);

        (await cache.RefreshAsync<string>(key, TimeSpan.FromHours(1), null, Ct)).Should().BeTrue();
        (await cache.RefreshAsync<string>(other, DateTimeOffset.MaxValue, null, Ct)).Should().BeTrue();

        (await Raw.KeyTimeToLiveAsync(Find(key)))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(30));
        (await Raw.KeyTimeToLiveAsync(Find(other))).Should().BeNull("persisted");
        (await cache.RefreshAsync<string>($"it-{Guid.NewGuid():N}", TimeSpan.FromHours(1), null, Ct)).Should().BeFalse("a missing key has nothing to refresh");
    }

    [Fact]
    public async Task A_refresh_without_scripts_that_is_not_awaited_is_applied()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = (await Node("a", scripts: false, Grace)).Cache;
        var key = $"it-{Guid.NewGuid():N}";
        await cache.SetAsync(key, "v", TimeSpan.FromMinutes(1), Ct);

        (await cache.RefreshAsync<string>(key, TimeSpan.FromHours(1), null, Ct)).Should().BeFalse("a refresh that is not awaited answers nothing");

        // One connection runs its commands in order, so this read answers after the refresh it may not have awaited.
        _ = await cache.GetAsync<string>(key, Ct);
        var left = await Raw.KeyTimeToLiveAsync(Find(key));
        left!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(30));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_of_the_lifetime_puts_a_tombstone_that_lost_its_grace_back(bool persisted)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = (await Node("a", scripts: false, Grace)).Cache;
        var key = $"it-{Guid.NewGuid():N}";
        await cache.SetAsync(key, "v", Ttl, Ct);
        await cache.RemoveAsync<string>(key, Ct);
        var redisKey = Find(key);
        _ = persisted ? await Raw.KeyPersistAsync(redisKey) : await Raw.KeyExpireAsync(redisKey, TimeSpan.FromMinutes(10));

        (await cache.TimeToLiveAsync<string>(key, Ct)).Should().BeNull();

        (await Raw.KeyTimeToLiveAsync(redisKey)).Should().NotBeNull().And.Subject!.Value.Should().BeLessThanOrEqualTo(Grace);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task A_redis_only_GetOrAdd_does_not_put_back_a_value_removed_while_its_generator_ran(bool scripts, int shape)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var a = (await Node("a", scripts, Grace)).Cache;
        var b = (await Node("b", scripts, Grace)).Cache;
        var key = $"it-{Guid.NewGuid():N}";
        var (generated, release) = Gate();
        async Task Wait()
        {
            generated.TrySetResult();
            await release.Task;
        }

        var slow = shape switch
        {
            0 => a.GetOrAddAsync<string>(key, async _ => { await Wait(); return "from-a"; }, Ttl, Ct).AsTask(),
            1 => a.GetOrAddWithExpirationAsync<string>(key, async _ => { await Wait(); return new GeneratedValue<string>("from-a", Clock.ToDateTimeOffset(Ttl)); }, Ct).AsTask(),
            _ => BatchAsync(a, key, Wait),
        };
        await generated.Task.WaitAsync(Ct);

        Clock.Advance(Grace);
        await b.RemoveAsync<string>(key, Ct);
        release.SetResult();

        (await slow).Should().Be("from-a", "the caller gets what its generator made");
        TombstoneValue.Is(await Raw.StringGetAsync(Find(key))).Should().BeTrue("the store left the tombstone alone");
        (await a.GetAsync<string>(key, Ct)).Should().BeNull();
    }

    [Theory]
    [InlineData(false, 0, "none")]
    [InlineData(true, 0, "none")]
    [InlineData(false, 1, "none")]
    [InlineData(true, 1, "none")]
    [InlineData(false, 2, "none")]
    [InlineData(true, 2, "none")]
    [InlineData(false, 3, "none")]
    [InlineData(true, 3, "none")]
    [InlineData(false, 0, "removal")]
    [InlineData(true, 0, "removal")]
    [InlineData(false, 1, "removal")]
    [InlineData(true, 1, "removal")]
    [InlineData(false, 2, "removal")]
    [InlineData(true, 2, "removal")]
    [InlineData(false, 3, "removal")]
    [InlineData(true, 3, "removal")]
    [InlineData(false, 0, "peer")]
    [InlineData(true, 0, "peer")]
    [InlineData(false, 1, "peer")]
    [InlineData(true, 1, "peer")]
    [InlineData(false, 2, "peer")]
    [InlineData(true, 2, "peer")]
    [InlineData(false, 3, "peer")]
    [InlineData(true, 3, "peer")]
    public async Task A_value_nothing_reads_is_replaced_by_the_fill_only_while_it_is_still_there(bool scripts, int shape, string change)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var redis = (RedisCache)(await Node("a", scripts, Grace)).Provider.CreateCache();
        var a = (ICache)redis;
        var b = (await Node("b", scripts, Grace)).Cache;
        var key = $"it-{Guid.NewGuid():N}";
        await a.SetAsync(key, "seed", Ttl, Ct);
        var redisKey = Find(key);
        await Raw.KeyDeleteAsync(redisKey);
        var (generated, release) = Gate();
        async Task Wait()
        {
            generated.TrySetResult();
            await release.Task;
        }

        redis.BeforeUnreadableReplace = async () =>
        {
            redis.BeforeUnreadableReplace = null;
            switch (change)
            {
                case "removal":
                    Clock.Advance(Grace);
                    await b.RemoveAsync<string>(key, Ct);
                    break;
                case "peer":
                    await b.SetAsync(key, "from-b", Ttl, Ct);
                    break;
            }
        };

        DateTimeOffset? expiration = null;
        var slow = shape switch
        {
            0 => a.GetOrAddAsync<string>(key, async _ => { await Wait(); return "from-a"; }, Ttl, Ct).AsTask(),
            1 => a.GetOrAddWithExpirationAsync<string>(key, async _ => { await Wait(); return new GeneratedValue<string>("from-a", Clock.ToDateTimeOffset(Ttl)); }, Ct).AsTask(),
            2 => Task.Run(async () =>
            {
                var result = await a.GetOrAddResultAsync<string>(key, async _ => { await Wait(); return "from-a"; }, Ttl, null, Ct);
                expiration = result.Expiration;
                return result.Value;
            }),
            _ => BatchAsync(a, key, Wait),
        };
        await generated.Task.WaitAsync(Ct);
        await Raw.StringSetAsync(redisKey, "not json{", Ttl);
        release.SetResult();

        var answer = await slow;

        redis.BeforeUnreadableReplace.Should().BeNull("the fill read the unreadable value and went to replace it");
        switch (change)
        {
            case "removal":
                answer.Should().Be("from-a", "the caller gets what its generator made");
                TombstoneValue.Is(await Raw.StringGetAsync(redisKey)).Should().BeTrue("the removal that landed after the unreadable value was read stands");
                (await a.GetAsync<string>(key, Ct)).Should().BeNull();
                expiration.Should().BeNull("nothing was stored, so there is no deadline to report");
                break;
            case "peer":
                (await Raw.StringGetAsync(redisKey)).ToString().Should().Contain("from-b", "the peer's value is not written over");
                if (shape != 3)
                {
                    answer.Should().Be("from-b", "the fill decided again and found the value");
                }

                break;
            default:
                answer.Should().Be("from-a");
                (await Raw.StringGetAsync(redisKey)).ToString().Should().Contain("from-a", "nothing changed the bytes, so the fill replaced them");
                (await a.GetAsync<string>(key, Ct)).Should().Be("from-a");
                (await Raw.KeyTimeToLiveAsync(redisKey))!.Value.Should().BeLessThanOrEqualTo(Ttl);
                if (shape == 2)
                {
                    expiration.Should().NotBeNull();
                }

                break;
        }
    }
    [Theory]
    [InlineData(false, 1, "from-a")]
    [InlineData(true, 1, "from-a")]
    [InlineData(false, 10, "unreadable")]
    [InlineData(true, 10, "unreadable")]
    public async Task A_batch_fill_decides_again_after_the_unreadable_value_changed_and_stops_after_three_attempts(bool scripts, int changes, string stays)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var redis = (RedisCache)(await Node("a", scripts, Grace)).Provider.CreateCache();
        var a = (ICache)redis;
        var key = $"it-{Guid.NewGuid():N}";
        await a.SetAsync(key, "seed", Ttl, Ct);
        var redisKey = Find(key);
        await Raw.StringSetAsync(redisKey, "not json{", Ttl);
        var calls = 0;
        redis.BeforeUnreadableReplace = async () =>
        {
            calls++;
            if (calls <= changes)
            {
                await Raw.StringSetAsync(redisKey, $"still not json{{{calls}", Ttl);
            }
        };

        var answer = await BatchAsync(a, key, () => Task.CompletedTask);

        answer.Should().Be("from-a");
        if (stays == "from-a")
        {
            calls.Should().Be(2, "the lost compare sent the key through another attempt");
            (await Raw.StringGetAsync(redisKey)).ToString().Should().Contain("from-a");
        }
        else
        {
            calls.Should().Be(3, "the retry limit holds under repeated contention");
            (await Raw.StringGetAsync(redisKey)).ToString().Should().StartWith("still not json");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_redis_only_GetOrAdd_answers_with_the_value_a_peer_stored_meanwhile(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var a = (await Node("a", scripts, Grace)).Cache;
        var b = (await Node("b", scripts, Grace)).Cache;
        var key = $"it-{Guid.NewGuid():N}";
        var (generated, release) = Gate();
        async Task<string?> Generate(CancellationToken _)
        {
            generated.SetResult();
            await release.Task;
            return "from-a";
        }

        var slow = a.GetOrAddAsync<string>(key, Generate, Ttl, Ct).AsTask();
        await generated.Task.WaitAsync(Ct);

        await b.SetAsync(key, "from-b", Ttl, Ct);
        release.SetResult();

        (await slow).Should().Be("from-b", "a fill that finds a value answers with it, as a layered one does");
        (await b.GetAsync<string>(key, Ct)).Should().Be("from-b");
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

    private static async Task<string?> BatchAsync(ICache cache, string key, Func<Task> wait)
    {
        var result = await cache.GetOrAddAsync<string, int>(
            [new(key, 1)],
            async (states, _) =>
            {
                await wait();
                return [.. states.Select(s => new KeyValuePair<int, string?>(s, "from-a"))];
            },
            Ttl,
            null,
            Ct);
        return result.Single().Value;
    }

    private static (TaskCompletionSource Started, TaskCompletionSource Release) Gate() =>
        (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private IServer Server()
    {
        _ = Raw;
        return _raw!.GetServer(_raw.GetEndPoints()[0]);
    }

    private RedisKey Find(string key, bool allowMissing = false)
    {
        var found = Server().Keys(pattern: $"*{key}*").ToArray();
        return found.Length == 0 && allowMissing ? $"missing:{key}" : found.Single();
    }

    private async Task<TestNode> Node(string source, bool scripts, TimeSpan? grace, Action<RedisCacheOptions>? configure = null, bool serializers = false, TimeProvider? clock = null, ILogger? logs = null)
    {
        var telemetry = new Telemetry.RecordingTelemetryProvider();
        var provider = new ServiceCollection()
            .AddSingleton<ICachingTelemetryProvider>(telemetry)
            .AddSingleton(clock ?? Clock)
            .AddLogging(b =>
            {
                if (logs is not null)
                {
                    b.AddProvider(new FixedLoggerProvider(logs));
                }
            })
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedisPubSub(o => { o.Enabled = true; o.SubscriberDueTime = TimeSpan.Zero; });
                    b.AddRedis(o =>
                    {
                        o.ReadScriptsEnabled = scripts;
                        o.RemoveTombstone = grace;
                        configure?.Invoke(o);
                    });
                    b.AddInMemoryRedis(o => o.LocalMaxExpiration = TimeSpan.FromMinutes(1));
                    if (serializers)
                    {
                        b.AddValueSerializer("fast", [0x10], _ => new PassthroughValueSerializer(0x10));
                    }
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.DefaultTopic = KnownTopicNames.RedisPubSub;
                    o.SourceUri = new Uri($"urn:{source}-{_app}");
                })
            .BuildServiceProvider();
        _nodes.Add(provider);
        var topic = (RedisPubSubTopic<ICacheEvent>)provider.GetRequiredService<ITopicFactory>().Get(KnownTopicNames.RedisPubSub)
            .Create(new DefaultTopicKeyStrategy().GetTopicKey(typeof(string)));
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Ready() => subscribed.TrySetResult();
        topic.Subscribed += Ready;
        using var subscription = topic.Subscribe(Substitute.For<IObserver<ICacheEvent>>());
        try
        {
            if (!topic.IsSubscribed)
            {
                await subscribed.Task.WaitAsync(Ct);
            }
        }
        finally
        {
            topic.Subscribed -= Ready;
        }
        return new TestNode(provider, telemetry);
    }

    private async Task AgeAsync(string key, TimeSpan age)
    {
        Clock.Advance(age);
        await Raw.KeyExpireAsync(Find(key), Grace - age);
    }

    private sealed class FixedLoggerProvider(ILogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }

    private sealed class TestNode(ServiceProvider services, Telemetry.RecordingTelemetryProvider telemetry)
    {
        public ICache Cache { get; } = services.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);

        public ICache Layered { get; } = services.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemoryRedis);

        public RedisCacheProvider Provider => services.GetServices<ICacheProvider>().OfType<RedisCacheProvider>().Single();

        public string[] Unreadable() =>
            [.. telemetry.Metrics.Where(m => m.Name == ValueReader.UnreadableMetric).Select(m => m.Properties!["format"]?.ToString() ?? string.Empty)];
    }
}
