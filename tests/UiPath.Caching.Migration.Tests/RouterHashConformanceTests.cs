using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

// A routed hash cache over a real server: what the older client's adapter writes and what the library path writes are the same hash, field by field and deadline by deadline.
// The adapter here is a plausible old layout, one envelope per field; the application repeats these checks with its own adapter.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class RouterHashConformanceTests(RedisFixture fixture) : IAsyncLifetime
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.";

    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);

    private readonly string _cacheName = $"Rh{Guid.NewGuid():N}"[..12];
    private readonly string _app = $"rh{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];
    private DateTimeOffset _serverTime;
    private ConnectionMultiplexer? _redis;
    private bool _library;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TimeProvider Clock => _nodes[^1].GetRequiredService<TimeProvider>();

    private IDatabase Db => _redis!.GetDatabase();

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
            _serverTime = await _redis.GetServer(_redis.GetEndPoints()[0]).TimeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            await node.DisposeAsync();
        }

        if (_redis is not null)
        {
            foreach (var key in _redis.GetServer(_redis.GetEndPoints()[0]).Keys(pattern: $"h:{_cacheName},c:*"))
            {
                await Db.KeyDeleteAsync(key);
            }

            await _redis.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_hash_the_source_wrote_reads_equal_through_the_library_with_the_same_deadline()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var cache = Cache(NewNode());
        var deadline = Clock.ToDateTimeOffset(Life);

        _library = false;
        await cache.SetAsync("k", Fields(("a", "1"), ("b", "2,3")), deadline, Ct);
        _library = true;

        var read = await cache.GetAsync("k", Ct);
        read.Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2,3")));
        (await cache.GetAsync("k", ["b"], Ct)).Keys.Should().Equal("b");
        (await cache.ExpireTimeAsync("k", Ct))!.Value.Should().BeCloseTo(deadline, TimeSpan.FromSeconds(1));
        (await cache.ContainsAsync("k", Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_hash_the_library_wrote_has_the_fields_bytes_and_deadline_of_the_sources_write_of_the_same_fields()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var cache = Cache(NewNode());
        var deadline = Clock.ToDateTimeOffset(Life);
        var fields = Fields(("a", "1"), ("b", "2,3"));

        _library = false;
        await cache.SetAsync("viasource", fields, deadline, Ct);
        _library = true;
        await cache.SetAsync("vialibrary", fields, deadline, Ct);

        await SameHashAsync("viasource", "vialibrary");
        _library = false;
        (await cache.GetAsync("vialibrary", Ct)).Should().BeEquivalentTo(fields, "the source reads what the library wrote");
    }

    [Fact]
    public async Task A_merge_a_field_delete_and_a_refresh_leave_the_same_hash_on_either_path()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var cache = Cache(NewNode());
        var deadline = Clock.ToDateTimeOffset(Life);
        var later = Clock.ToDateTimeOffset(TimeSpan.FromHours(2));
        var merge = new HashCacheEntryOptions(ExpireTime: deadline, SetOption: HashCacheSetOption.HashReplace);

        foreach (var (library, key) in new[] { (false, "viasource"), (true, "vialibrary") })
        {
            _library = library;
            await cache.SetAsync(key, Fields(("a", "1"), ("b", "2"), ("c", "3")), deadline, Ct);
            (await Db.HashLengthAsync(RedisKeyOf(key))).Should().Be(3, "after set " + key);
            (await cache.SetAsync(key, Fields(("b", "20"), ("d", "4")), merge, Ct)).Should().BeTrue();
            (await Db.HashLengthAsync(RedisKeyOf(key))).Should().Be(4, "after merge " + key);
            (await cache.RemoveFieldsAsync(key, ["a"], Ct)).Should().BeTrue();
            (await Db.HashLengthAsync(RedisKeyOf(key))).Should().Be(3, "after remove " + key);
        }

        await SameHashAsync("viasource", "vialibrary");
        _library = true;
        (await cache.RefreshAsync("vialibrary", later, Ct)).Should().BeTrue();
        (await Db.KeyTimeToLiveAsync(RedisKeyOf("vialibrary")))!.Value.Should().BeCloseTo(later - Clock.GetUtcNow(), TimeSpan.FromSeconds(5));
        (await cache.GetAsync("vialibrary", Ct)).Keys.Should().BeEquivalentTo("b", "c", "d");
    }

    [Fact]
    public async Task A_deadline_of_no_expiry_leaves_no_ttl_on_either_path()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var cache = Cache(NewNode());

        foreach (var (library, key) in new[] { (false, "viasource"), (true, "vialibrary") })
        {
            _library = library;
            await cache.SetAsync(key, Fields(("a", "1")), DateTimeOffset.MaxValue, Ct);
            (await Db.KeyTimeToLiveAsync(RedisKeyOf(key))).Should().BeNull(library ? "library" : "source");
        }
    }

    [Fact]
    public async Task A_selected_read_answers_what_the_native_read_answers_for_every_kind_of_selector()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var node = NewNode();
        var routed = (RoutedHashCache<List<long>>)Cache(node);
        await RouterTestBase.WaitForLibraryAsync(node.GetRequiredService<IRouter>(), Ct);

        _library = true;
        IHashCache<List<long>> surface = routed;
        var native = ((LibraryHashPathClient<List<long>>)routed.Library).Cache!;
        await surface.SetAsync(new CacheKey("sel"), Fields(("a", "1"), ("b", "2,3")), Life, Ct);
        await Db.HashSetAsync(RedisKeyOf("sel"), "bad", "{not an envelope");

        var selectors = new string[]?[] { null, [], ["a"], ["a", "missing"], ["missing"], ["a", "b"], ["a", "bad"], ["bad"] };
        foreach (var selector in selectors)
        {
            var expected = selector is null
                ? await native.GetAsync<List<long>>(CacheKey.Verbatim(RedisKeyOf("sel")), (CachePolicy?)null, Ct)
                : await native.GetAsync<List<long>>(CacheKey.Verbatim(RedisKeyOf("sel")), selector, null, Ct);
            var routedRead = selector is null ? await surface.GetAsync(new CacheKey("sel"), Ct) : await surface.GetAsync(new CacheKey("sel"), selector, Ct);

            routedRead.Should().BeEquivalentTo(expected, $"selector [{string.Join(",", selector ?? ["<null>"])}]");
        }

        var absent = await surface.GetAsync(new CacheKey("nokey"), ["a"], Ct);
        absent.Should().BeEquivalentTo(await native.GetAsync<List<long>>(CacheKey.Verbatim(RedisKeyOf("nokey")), ["a"], null, Ct));
    }

    // A field is its name and its numbers as text: "2,3" is the list 2, 3.
    [Fact]
    public async Task A_host_with_a_hash_registration_starts_the_library_and_gives_the_cache_the_plain_path_with_the_shared_tier()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var node = NewNode();
        var routed = (RoutedHashCache<List<long>>)Cache(node);
        await RouterTestBase.WaitForLibraryAsync(node.GetRequiredService<IRouter>(), Ct);

        routed.IsSubscribed.Should().BeTrue("the library start attaches the hash cache");
        var plain = routed.Library.Should().BeOfType<LibraryHashPathClient<List<long>>>().Subject;
        plain.Tier.Should().NotBeNull();
        plain.Cache.Should().NotBeNull();
    }

    private static Dictionary<string, List<long>?> Fields(params (string Name, string Numbers)[] fields) =>
        fields.ToDictionary(f => f.Name, f => (List<long>?)[.. f.Numbers.Split(',').Select(long.Parse)]);

    private static IHashCache<List<long>> Cache(ServiceProvider node) => node.GetRequiredService<IRouter>().GetHash<List<long>>("lists");

    private string RedisKeyOf(string key) => $"h:{_cacheName},c:{key}";

    private async Task SameHashAsync(string expected, string actual)
    {
        var a = (await Db.HashGetAllAsync(RedisKeyOf(expected))).ToDictionary(e => e.Name.ToString(), e => (byte[]?)e.Value ?? []);
        var b = (await Db.HashGetAllAsync(RedisKeyOf(actual))).ToDictionary(e => e.Name.ToString(), e => (byte[]?)e.Value ?? []);

        a.Should().NotBeEmpty($"{expected} was written");
        b.Keys.Should().BeEquivalentTo(a.Keys, "the same field names");
        foreach (var (name, bytes) in a)
        {
            b[name].Should().Equal(bytes, $"the same bytes in field {name}");
        }

        var ttlA = await Db.KeyTimeToLiveAsync(RedisKeyOf(expected));
        var ttlB = await Db.KeyTimeToLiveAsync(RedisKeyOf(actual));
        (ttlA is null).Should().Be(ttlB is null);
        if (ttlA is { } left && ttlB is { } right)
        {
            right.Should().BeCloseTo(left, TimeSpan.FromSeconds(2));
        }
    }

    private ServiceProvider NewNode()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = _app,
            ["Caching:SourceUri"] = $"urn:router-hash-{_app}",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = $"{fixture.ConnectionString},name=rh{_app}",
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
            ["Caching:DefaultTopic"] = KnownTopicNames.RedisStreams,
            ["Caching:Broadcast:RedisPubSub:Enabled"] = "false",
            ["Caching:Broadcast:RedisStreams:Enabled"] = "true",
        }).Build();
        var source = new LegacyHashSource(_redis!, _cacheName);
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(new ManualTimeProvider(_serverTime));
        services.AddCaching(configuration, builder => builder.AddRedisConnection());
        services.AddCachingRouter(
            configuration,
            router => router
                .AddHash(new RouterHashCacheRegistration<List<long>> { Name = "lists", Group = "g", Source = source, RedisKey = source.RedisKey, DefaultExpiry = Life })
                .UseLibraryWhen(_ => _library)
                .UseEnvelope(new LegacyEnvelopeProxy()));
        var provider = services.BuildServiceProvider();
        _nodes.Add(provider);
        _ = provider.GetRequiredService<IRouter>();
        return provider;
    }
}
