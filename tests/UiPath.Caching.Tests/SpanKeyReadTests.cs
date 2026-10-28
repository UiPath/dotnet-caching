using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>The <c>Span&lt;char&gt;</c> reads answer as the <see cref="CacheKey"/> reads do, over real in-memory tiers.</summary>
public class SpanKeyReadTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_bare_default_key_still_binds_to_the_CacheKey_overload()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        var hash = Substitute.For<IHashCache>();
        var typedHash = Substitute.For<IHashCache<string>>();

        await cache.GetAsync<string>(default, null, Ct);
        await typed.GetAsync(default, Ct);
        await hash.GetItemAsync<string>(default, "f", null, Ct);
        await hash.GetAsync<string>(default, null, Ct);
        await typedHash.GetItemAsync(default, "f", Ct);
        await typedHash.GetAsync(default, Ct);
        await cache.ContainsAsync<string>(default, Ct);
        await typed.GetOrAddAsync(default, _ => Task.FromResult<string?>("v"), Ct);
        await hash.GetCacheEntryAsync<string>(default, null, Ct);

        await cache.Received(1).GetAsync<string>(Arg.Any<CacheKey>(), null, Ct);
        await typed.Received(1).GetAsync(Arg.Any<CacheKey>(), Ct);
        await hash.Received(1).GetItemAsync<string>(Arg.Any<CacheKey>(), "f", null, Ct);
        await hash.Received(1).GetAsync<string>(Arg.Any<CacheKey>(), null, Ct);
        await typedHash.Received(1).GetItemAsync(Arg.Any<CacheKey>(), "f", Ct);
        await typedHash.Received(1).GetAsync(Arg.Any<CacheKey>(), Ct);
        await cache.Received(1).ContainsAsync<string>(Arg.Any<CacheKey>(), Ct);
        await typed.Received(1).GetOrAddAsync(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<string?>>>(), Ct);
        await hash.Received(1).GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), null, Ct);
    }

    [Fact]
    public async Task A_cache_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.Cache();
        (await cache.SetAsync<string>(" User:42 ", "v", policy: null, Ct)).Should().BeTrue();

        SpanReads.Read<string>(cache, "User:42", Ct).Should().Be("v");
        SpanReads.Read<string>(cache, " user:42", Ct).Should().Be("v");
        SpanReads.Read<string>(cache, "user:43", Ct).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_cache_rejects_an_empty_span_as_it_rejects_an_empty_key(string key)
    {
        using var cache = InMemoryMultilayer.Cache();

        var act = () => SpanReads.Read<string>(cache, key, Ct);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task A_cache_reads_by_span_a_key_longer_than_the_stack_buffer()
    {
        using var cache = InMemoryMultilayer.Cache();
        var key = string.Concat(Enumerable.Repeat("k", 300));
        (await cache.SetAsync<string>(key, "v", policy: null, Ct)).Should().BeTrue();

        SpanReads.Read<string>(cache, key, Ct).Should().Be("v");
    }

    [Fact]
    public async Task A_cache_with_a_prefix_strategy_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        (await cache.SetAsync<string>("User:42", "v", policy: null, Ct)).Should().BeTrue();

        SpanReads.Read<string>(cache, "User:42", Ct).Should().Be("v");
    }

    [Fact]
    public async Task A_cache_with_a_custom_strategy_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new SuffixStrategy() });
        (await cache.SetAsync<string>("User:42", "v", policy: null, Ct)).Should().BeTrue();

        SpanReads.Read<string>(cache, "User:42", Ct).Should().Be("v");
    }

    [Fact]
    public async Task A_cache_with_a_strategy_that_declines_the_span_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new DecliningStrategy() });
        (await cache.SetAsync<string>("User:42", "v", policy: null, Ct)).Should().BeTrue();

        SpanReads.Read<string>(cache, "User:42", Ct).Should().Be("v");
    }

    [Fact]
    public async Task A_typed_cache_with_a_prefix_strategy_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await typed.SetAsync(" User:42 ", "v", Ct)).Should().BeTrue();

        SpanReads.Read(typed, "User:42", Ct).Should().Be("v");
        (await cache.GetAsync<string>("app:user:42", policy: null, Ct)).Should().Be("v");
    }

    [Fact]
    public async Task A_typed_cache_with_a_custom_strategy_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new SuffixStrategy());
        (await typed.SetAsync("User:42", "v", Ct)).Should().BeTrue();

        SpanReads.Read(typed, "User:42", Ct).Should().Be("v");
    }

    [Fact]
    public async Task An_outside_implementation_reads_by_span_through_its_CacheKey_overload()
    {
        var cache = new DictionaryCache();
        (await cache.SetAsync<string>("k", "v", policy: null, Ct)).Should().BeTrue();

        SpanReads.Read<string>(cache, " K ", Ct).Should().Be("v");
    }

    [Fact]
    public void A_proxied_cache_serves_a_span_read_through_its_CacheKey_overload()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        var hash = Substitute.For<IHashCache>();
        var typedHash = Substitute.For<IHashCache<string>>();
        IDictionary<string, string?> all = new Dictionary<string, string?> { ["f"] = "v" };
        cache.GetAsync<string>(new CacheKey("k"), null, Ct).Returns("v");
        typed.GetAsync(new CacheKey("k"), Ct).Returns("v");
        hash.GetItemAsync<string>(new CacheKey("k"), "f", null, Ct).Returns("v");
        hash.GetAsync<string>(new CacheKey("k"), null, Ct).Returns(all);
        typedHash.GetItemAsync(new CacheKey("k"), "f", Ct).Returns("v");
        typedHash.GetAsync(new CacheKey("k"), Ct).Returns(all);

        SpanReads.Read<string>(cache, " K ", Ct).Should().Be("v");
        SpanReads.Read(typed, " K ", Ct).Should().Be("v");
        SpanReads.ReadItem<string>(hash, " K ", "f", Ct).Should().Be("v");
        SpanReads.ReadAll<string>(hash, " K ", Ct).Should().BeSameAs(all);
        SpanReads.ReadItem(typedHash, " K ", "f", Ct).Should().Be("v");
        SpanReads.ReadAll(typedHash, " K ", Ct).Should().BeSameAs(all);
    }

    [Fact]
    public async Task A_typed_cache_with_a_proxied_strategy_reads_by_span_through_GetCacheKey()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions());
        var strategy = Substitute.For<ICacheKeyStrategy>();
        strategy.GetCacheKey<string>(Arg.Any<CacheKey>()).Returns(c => new CacheKey("app:" + c.Arg<CacheKey>().Name));
        var typed = new Cache<string>(cache, strategy);
        (await typed.SetAsync("k", "v", Ct)).Should().BeTrue();

        SpanReads.Read(typed, "k", Ct).Should().Be("v");
    }

    [Fact]
    public void A_typed_cache_over_a_proxied_cache_serves_a_span_read_through_the_composed_CacheKey()
    {
        var inner = Substitute.For<ICache>();
        var innerHash = Substitute.For<IHashCache>();
        inner.GetAsync<string>(new CacheKey("app:k"), Arg.Any<CachePolicy?>(), Ct).Returns("v");
        innerHash.GetItemAsync<string>(new CacheKey("app:k"), "f", Arg.Any<CachePolicy?>(), Ct).Returns("v");

        SpanReads.Read(new Cache<string>(inner, new PrefixCacheKeyStrategy("app")), "k", Ct).Should().Be("v");
        SpanReads.ReadItem(new HashCache<string>(innerHash, new PrefixCacheKeyStrategy("app")), "k", "f", Ct).Should().Be("v");
    }

    [Fact]
    public async Task A_hash_cache_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var values = new Dictionary<string, string?> { ["name"] = "n", ["Other"] = "o" };
        (await cache.SetAsync<string>(" User:42 ", values, policy: null, Ct)).Should().BeTrue();

        SpanReads.ReadItem<string>(cache, "User:42", "name", Ct).Should().Be("n");
        SpanReads.ReadItem<string>(cache, "User:42", "NAME", Ct).Should().Be(await cache.GetItemAsync<string>("User:42", "NAME", policy: null, Ct));
        SpanReads.ReadItem<string>(cache, "User:42", "missing", Ct).Should().BeNull();
        SpanReads.ReadItem<string>(cache, "User:43", "name", Ct).Should().BeNull();
        SpanReads.ReadAll<string>(cache, "User:42", Ct).Should().BeEquivalentTo(values);
        SpanReads.ReadAll<string>(cache, "User:43", Ct).Should().BeEmpty();
    }

    [Fact]
    public async Task A_typed_hash_cache_with_a_prefix_strategy_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.HashCache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var typed = new HashCache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var values = new Dictionary<string, string?> { ["name"] = "n" };
        (await typed.SetAsync("User:42", values, Ct)).Should().BeTrue();

        SpanReads.ReadItem(typed, "User:42", "name", Ct).Should().Be("n");
        SpanReads.ReadAll(typed, "User:42", Ct).Should().BeEquivalentTo(values);
        (await cache.GetItemAsync<string>("app:user:42", "name", policy: null, Ct)).Should().Be("n");
    }

    [Fact]
    public async Task A_cancelled_span_read_throws_as_the_key_read_does_even_on_a_warm_hit()
    {
        using var cache = InMemoryMultilayer.Cache();
        using var hash = InMemoryMultilayer.HashCache();
        (await cache.SetAsync<string>("user:42", "v", policy: null, Ct)).Should().BeTrue();
        (await hash.SetAsync<string>("user:42", new Dictionary<string, string?> { ["name"] = "n" }, policy: null, Ct)).Should().BeTrue();
        var cancelled = new CancellationToken(canceled: true);

        var byKey = () => cache.GetAsync<string>("user:42", policy: null, cancelled);
        var bySpan = () => SpanReads.Read<string>(cache, "user:42", cancelled);
        var itemBySpan = () => SpanReads.ReadItem<string>(hash, "user:42", "name", cancelled);
        var allBySpan = () => SpanReads.ReadAll<string>(hash, "user:42", cancelled);

        byKey.Should().Throw<OperationCanceledException>();
        bySpan.Should().Throw<OperationCanceledException>();
        itemBySpan.Should().Throw<OperationCanceledException>();
        allBySpan.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void A_cancelled_span_read_is_cancelled_before_the_strategy_runs()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new ThrowingStrategy() });
        using var hash = InMemoryMultilayer.HashCache(new InMemoryCacheOptions { CacheKeyStrategy = new ThrowingStrategy() });
        var cancelled = new CancellationToken(canceled: true);

        var bySpan = () => SpanReads.Read<string>(cache, "user:42", cancelled);
        var itemBySpan = () => SpanReads.ReadItem<string>(hash, "user:42", "f", cancelled);
        var allBySpan = () => SpanReads.ReadAll<string>(hash, "user:42", cancelled);

        bySpan.Should().Throw<OperationCanceledException>();
        itemBySpan.Should().Throw<OperationCanceledException>();
        allBySpan.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void An_empty_span_read_with_a_cancelled_token_fails_as_the_key_read_fails()
    {
        using var cache = InMemoryMultilayer.Cache();
        using var hash = InMemoryMultilayer.HashCache();
        var cancelled = new CancellationToken(canceled: true);

        var byKey = Record.Exception(() => cache.GetAsync<string>("", policy: null, cancelled).AsTask().GetAwaiter().GetResult());
        var bySpan = Record.Exception(() => SpanReads.Read<string>(cache, "", cancelled));
        var itemByKey = Record.Exception(() => hash.GetItemAsync<string>("", "f", policy: null, cancelled).AsTask().GetAwaiter().GetResult());
        var itemBySpan = Record.Exception(() => SpanReads.ReadItem<string>(hash, "", "f", cancelled));

        byKey.Should().BeOfType<ArgumentNullException>();
        bySpan.Should().BeOfType(byKey!.GetType());
        itemBySpan.Should().BeOfType(itemByKey!.GetType());
    }

    [Fact]
    public void The_null_caches_answer_a_span_read_with_nothing()
    {
        SpanReads.Read<string>(NullCache.Instance, "k", Ct).Should().BeNull();
        SpanReads.ReadItem<string>(NullHashCache.Instance, "k", "f", Ct).Should().BeNull();
        SpanReads.ReadAll<string>(NullHashCache.Instance, "k", Ct).Should().BeEmpty();
    }

    [Fact]
    public async Task A_cache_reads_the_expiry_by_span_as_it_reads_it_by_key()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await cache.SetAsync<string>("user:42", "v", Ttl, policy: null, Ct)).Should().BeTrue();
        (await typed.SetAsync("user:42", "v", Ttl, Ct)).Should().BeTrue();

        SpanReads.ExpireTime<string>(cache, " User:42 ", Ct).Should().Be(await cache.ExpireTimeAsync<string>("user:42", Ct)).And.NotBeNull();
        SpanReads.TimeToLive<string>(cache, "user:42", Ct)!.Value.Should().BeCloseTo((await cache.TimeToLiveAsync<string>("user:42", Ct))!.Value, Slack);
        SpanReads.ExpireTime(typed, "user:42", Ct).Should().Be(await typed.ExpireTimeAsync("user:42", Ct)).And.NotBeNull();
        SpanReads.TimeToLive(typed, "user:42", Ct)!.Value.Should().BeCloseTo((await typed.TimeToLiveAsync("user:42", Ct))!.Value, Slack);
        SpanReads.ExpireTime<string>(cache, "user:43", Ct).Should().Be(await cache.ExpireTimeAsync<string>("user:43", Ct));
        SpanReads.TimeToLive<string>(cache, "user:43", Ct).Should().Be(await cache.TimeToLiveAsync<string>("user:43", Ct));
        SpanReads.ExpireTime(typed, "user:43", Ct).Should().BeNull();
        SpanReads.TimeToLive(typed, "user:43", Ct).Should().BeNull();
    }

    [Fact]
    public async Task A_hash_cache_reads_fields_metadata_and_expiry_by_span_as_it_reads_them_by_key()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var values = new Dictionary<string, string?> { ["name"] = "n", ["Other"] = "o" };
        (await cache.SetAsync<string>("user:42", values, Ttl, policy: null, Ct)).Should().BeTrue();
        (await cache.SetMetadataAsync<string>("user:42", new Dictionary<string, string?> { ["m"] = "1" }, Ct)).Should().BeTrue();
        string[] fields = ["NAME", "missing"];

        SpanReads.ReadFields<string>(cache, " User:42 ", fields, Ct).Should().BeEquivalentTo(await cache.GetAsync<string>("user:42", fields, policy: null, Ct)).And.ContainKey("name");
        SpanReads.ReadFields<string>(cache, "user:43", fields, Ct).Should().BeEquivalentTo(await cache.GetAsync<string>("user:43", fields, policy: null, Ct)).And.BeEmpty();
        SpanReads.Metadata<string>(cache, "user:42", Ct).Should().BeEquivalentTo(await cache.GetMetadataAsync<string>("user:42", Ct));
        SpanReads.Metadata<string>(cache, "user:43", Ct).Should().BeEquivalentTo(await cache.GetMetadataAsync<string>("user:43", Ct));
        SpanReads.ExpireTime<string>(cache, "user:42", Ct).Should().Be(await cache.ExpireTimeAsync<string>("user:42", Ct)).And.NotBeNull();
        SpanReads.TimeToLive<string>(cache, "user:42", Ct)!.Value.Should().BeCloseTo((await cache.TimeToLiveAsync<string>("user:42", Ct))!.Value, Slack);
        SpanReads.ExpireTime<string>(cache, "user:43", Ct).Should().Be(await cache.ExpireTimeAsync<string>("user:43", Ct));
        SpanReads.TimeToLive<string>(cache, "user:43", Ct).Should().Be(await cache.TimeToLiveAsync<string>("user:43", Ct));
    }

    [Fact]
    public async Task A_typed_hash_cache_reads_fields_metadata_and_expiry_by_span_as_it_reads_them_by_key()
    {
        using var cache = InMemoryMultilayer.HashCache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var typed = new HashCache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await typed.SetAsync("user:42", new Dictionary<string, string?> { ["name"] = "n", ["other"] = "o" }, Ttl, Ct)).Should().BeTrue();
        (await typed.SetMetadataAsync("user:42", new Dictionary<string, string?> { ["m"] = "1" }, Ct)).Should().BeTrue();
        string[] fields = ["name"];

        SpanReads.ReadFields(typed, "user:42", fields, Ct).Should().BeEquivalentTo(await typed.GetAsync("user:42", fields, Ct)).And.ContainSingle();
        SpanReads.ReadFields(typed, "user:43", fields, Ct).Should().BeEmpty();
        SpanReads.Metadata(typed, "user:42", Ct).Should().BeEquivalentTo(await typed.GetMetadataAsync("user:42", Ct));
        SpanReads.Metadata(typed, "user:43", Ct).Should().BeEquivalentTo(await typed.GetMetadataAsync("user:43", Ct));
        SpanReads.ExpireTime(typed, "user:42", Ct).Should().Be(await typed.ExpireTimeAsync("user:42", Ct)).And.NotBeNull();
        SpanReads.TimeToLive(typed, "user:42", Ct)!.Value.Should().BeCloseTo((await typed.TimeToLiveAsync("user:42", Ct))!.Value, Slack);
        SpanReads.ExpireTime(typed, "user:43", Ct).Should().BeNull();
        SpanReads.TimeToLive(typed, "user:43", Ct).Should().BeNull();
    }

    [Fact]
    public void The_null_caches_answer_the_span_field_metadata_and_expiry_reads_with_nothing()
    {
        SpanReads.TimeToLive<string>(NullCache.Instance, "k", Ct).Should().BeNull();
        SpanReads.ExpireTime<string>(NullCache.Instance, "k", Ct).Should().BeNull();
        SpanReads.ReadFields<string>(NullHashCache.Instance, "k", ["f"], Ct).Should().BeEmpty();
        SpanReads.Metadata<string>(NullHashCache.Instance, "k", Ct).Should().BeNull();
        SpanReads.TimeToLive<string>(NullHashCache.Instance, "k", Ct).Should().BeNull();
        SpanReads.ExpireTime<string>(NullHashCache.Instance, "k", Ct).Should().BeNull();
    }

#if NET9_0_OR_GREATER
    [Fact]
    public async Task A_span_read_does_not_serve_a_local_hit_when_the_connection_drops_during_the_lookup()
    {
        var inner = Substitute.For<ICache, IConnectionState>();
        var state = (IConnectionState)inner;
        state.IsConnected.Returns(true);
        inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(true);
        var strategy = new DroppingStrategy(() =>
        {
            state.IsConnected.Returns(false);
            state.OnConnectionFailed += Raise.Event<EventHandler>(state, EventArgs.Empty);
        });
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = strategy, UseLocalOnlyWhenDisconnected = false }, inner, connectionMonitor: true);
        (await cache.SetAsync<string>("user:42", "v", policy: null, Ct)).Should().BeTrue();

        SpanReads.Read<string>(cache, "user:42", Ct).Should().BeNull("the key path removes a local entry once the tier is disconnected, and the span path must reach that decision too");
    }

    [Fact]
    public async Task A_hash_span_read_does_not_serve_a_local_hit_when_the_connection_drops_during_the_lookup()
    {
        var inner = Substitute.For<IHashCache, IConnectionState>();
        var state = (IConnectionState)inner;
        state.IsConnected.Returns(true);
        inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(true);
        var strategy = new DroppingStrategy(() =>
        {
            state.IsConnected.Returns(false);
            state.OnConnectionFailed += Raise.Event<EventHandler>(state, EventArgs.Empty);
        });
        using var cache = InMemoryMultilayer.HashCache(new InMemoryCacheOptions { CacheKeyStrategy = strategy, UseLocalOnlyWhenDisconnected = false }, inner, connectionMonitor: true);
        (await cache.SetAsync<string>("user:42", new Dictionary<string, string?> { ["f"] = "v" }, policy: null, Ct)).Should().BeTrue();

        SpanReads.ReadItem<string>(cache, "user:42", "f", Ct).Should().BeNull("the key path removes a local entry once the tier is disconnected, and the span path must reach that decision too");
    }

    [Fact]
    public async Task A_warm_span_read_that_hits_locally_allocates_nothing()
    {
        using var cache = InMemoryMultilayer.Cache();
        (await cache.SetAsync<string>("user:42", "v", policy: null, Ct)).Should().BeTrue();

        AllocatedBy(() => SpanReads.Read<string>(cache, "user:42")).Should().Be(0);
        AllocatedBy(() => SpanReads.Read<string>(cache, " User:42 ")).Should().Be(0, "normalizing on the stack keeps the read on the fast path");
    }

    [Fact]
    public async Task A_warm_span_read_through_prefix_strategies_allocates_nothing()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await typed.SetAsync("user:42", "v", Ct)).Should().BeTrue();

        AllocatedBy(() => SpanReads.Read(typed, " User:42 ")).Should().Be(0);
    }

    [Fact]
    public async Task A_warm_span_read_through_custom_strategies_that_compose_by_span_allocates_nothing()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new SuffixStrategy() });
        var typed = new Cache<string>(cache, new SuffixStrategy());
        (await typed.SetAsync("user:42", "v", Ct)).Should().BeTrue();

        AllocatedBy(() => SpanReads.Read(typed, "user:42")).Should().Be(0, "the library lowercases the strategy's output as WithName does on the key path");
    }

    [Fact]
    public async Task A_warm_span_hash_read_that_hits_locally_allocates_nothing()
    {
        using var cache = InMemoryMultilayer.HashCache();
        (await cache.SetAsync<string>("user:42", new Dictionary<string, string?> { ["name"] = "n" }, policy: null, Ct)).Should().BeTrue();

        AllocatedBy(() => SpanReads.ReadItem<string>(cache, "user:42", "name")).Should().Be(0);
        AllocatedBy(() => SpanReads.ReadAll<string>(cache, "user:42")).Should().Be(0);
    }

    [Fact]
    public async Task A_warm_span_expiry_read_that_hits_locally_allocates_nothing()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await cache.SetAsync<string>("user:42", "v", Ttl, policy: null, Ct)).Should().BeTrue();
        (await typed.SetAsync("user:42", "v", Ttl, Ct)).Should().BeTrue();

        AllocatedBy(() => SpanReads.TimeToLive<string>(cache, " User:42 ")).Should().Be(0);
        AllocatedBy(() => SpanReads.ExpireTime<string>(cache, " User:42 ")).Should().Be(0);
        AllocatedBy(() => SpanReads.TimeToLive(typed, "user:42")).Should().Be(0);
        AllocatedBy(() => SpanReads.ExpireTime(typed, "user:42")).Should().Be(0);
    }

    [Fact]
    public async Task A_warm_span_hash_metadata_and_expiry_read_that_hits_locally_allocates_nothing()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var typed = new HashCache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await cache.SetAsync<string>("user:42", new Dictionary<string, string?> { ["name"] = "n" }, Ttl, policy: null, Ct)).Should().BeTrue();
        (await typed.SetAsync("user:42", new Dictionary<string, string?> { ["name"] = "n" }, Ttl, Ct)).Should().BeTrue();

        AllocatedBy(() => SpanReads.Metadata<string>(cache, "user:42")).Should().Be(0);
        AllocatedBy(() => SpanReads.TimeToLive<string>(cache, "user:42")).Should().Be(0);
        AllocatedBy(() => SpanReads.ExpireTime<string>(cache, "user:42")).Should().Be(0);
        AllocatedBy(() => SpanReads.Metadata(typed, "user:42")).Should().Be(0);
        AllocatedBy(() => SpanReads.TimeToLive(typed, "user:42")).Should().Be(0);
        AllocatedBy(() => SpanReads.ExpireTime(typed, "user:42")).Should().Be(0);
    }

    [Fact]
    public async Task A_warm_span_field_read_that_hits_locally_allocates_less_than_the_key_read()
    {
        using var cache = InMemoryMultilayer.HashCache();
        (await cache.SetAsync<string>("user:42", new Dictionary<string, string?> { ["name"] = "n" }, policy: null, Ct)).Should().BeTrue();
        string[] fields = ["name"];

        var bySpan = AllocatedBy(() => SpanReads.ReadFields<string>(cache, "user:42", fields));
        var byKey = AllocatedBy(() => SpanReads.Complete(cache.GetAsync<string>(new CacheKey("user:42"), fields, policy: null)));

        bySpan.Should().BeLessThan(byKey, "the span read selects the fields from the local entry without building the key or the entry options");
    }

    /// <summary>The fewest bytes the current thread allocates across a thousand warm calls in any of ten rounds: a per-call allocation shows in every round, a one-off from tiered JIT in one.</summary>
    private static long AllocatedBy(Action read)
    {
        var fewest = long.MaxValue;
        for (var round = 0; round < 10 && fewest > 0; round++)
        {
            for (var i = 0; i < 1_000; i++)
            {
                read();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1_000; i++)
            {
                read();
            }

            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return fewest;
    }
#endif

    /// <summary>Appends an upper-case suffix; the key path lowercases it through <c>WithName</c>, the span path through the library.</summary>
    private sealed class SuffixStrategy : ICacheKeyStrategy, ISpanCacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => key.WithName(key.Name + "#S");

        public bool TryGetCacheKey<T>(ReadOnlySpan<char> key, Span<char> destination, out int written)
        {
            written = key.Length + 2;
            if (written > destination.Length)
            {
                written = 0;
                return false;
            }

            key.CopyTo(destination);
            "#S".AsSpan().CopyTo(destination[key.Length..]);
            return true;
        }
    }

    /// <summary>Runs a callback while composing, which is after the read has sampled the connection and before it probes the local tier.</summary>
    private sealed class DroppingStrategy(Action onCompose) : ICacheKeyStrategy, ISpanCacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => key;

        public bool TryGetCacheKey<T>(ReadOnlySpan<char> key, Span<char> destination, out int written)
        {
            onCompose();
            key.CopyTo(destination);
            written = key.Length;
            return true;
        }
    }

    private sealed class ThrowingStrategy : ICacheKeyStrategy, ISpanCacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => throw new NotSupportedException("the strategy was reached");

        public bool TryGetCacheKey<T>(ReadOnlySpan<char> key, Span<char> destination, out int written) => throw new NotSupportedException("the strategy was reached");
    }

    private sealed class DecliningStrategy : ICacheKeyStrategy, ISpanCacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => key.WithName(key.Name + "#d");

        public bool TryGetCacheKey<T>(ReadOnlySpan<char> key, Span<char> destination, out int written)
        {
            written = 0;
            return false;
        }
    }
}
