using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class ConformanceRedisTests(RedisFixture fixture) : IAsyncLifetime
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.";

    private readonly string _cacheName = $"Cf{Guid.NewGuid():N}"[..12];
    private ConnectionMultiplexer? _redis;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KeyValuePair<string, List<long>>[] Samples =>
    [
        new("one", [1, 2, 3]),
        new("empty", []),
        new("zero", [0]),
        new(" Padded:Key{x} ", [long.MaxValue, long.MinValue]),
    ];

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_source_that_matches_the_library_conforms()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider(new LegacyEnvelopeProxy());

        var report = await RouterConformance.CheckAsync(provider, _cacheName, Samples, Ct);

        report.Passed.Should().BeTrue(report.ToString());
        report.Samples.Should().Be(4);
        (await _redis!.GetDatabase().KeyExistsAsync($"n:{_cacheName},c:one")).Should().BeFalse("the check deletes its sample keys");
    }

    [Fact]
    public async Task An_envelope_that_drifts_from_the_source_is_reported_without_keys_or_values()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider(new DriftingEnvelope());

        var report = await RouterConformance.CheckAsync(provider, _cacheName, Samples, Ct);

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(f => f.Contains("different bytes", StringComparison.Ordinal));
        report.Failures.Should().Contain(f => f.Contains("the source read", StringComparison.Ordinal));
        report.ToString().Should().NotContain("Padded").And.NotContain("9223372036854775807");
    }

    [Fact]
    public async Task TryAdd_on_the_library_path_creates_once_with_the_source_bytes_and_deadline()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider(new LegacyEnvelopeProxy());
        var router = provider.GetRequiredService<IRouter>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (router.Mode != RouterMode.Library && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, Ct);
        }

        router.Mode.Should().Be(RouterMode.Library);
        var cache = router.Get<List<long>>(_cacheName);
        var key = (RedisKey)$"n:{_cacheName},c:t";
        try
        {
            (await cache.TryAddAsync("t", [1, 2], CacheExpiry.After(TimeSpan.FromMinutes(10)), Ct)).Should().Be(AddOutcome.Added);
            (await cache.TryAddAsync("t", [9], CacheExpiry.After(TimeSpan.FromMinutes(10)), Ct)).Should().Be(AddOutcome.Exists);

            var db = _redis!.GetDatabase();
            ((byte[]?)await db.StringGetAsync(key)).Should().Equal(LegacyEnvelope.Write(new List<long> { 1, 2 }));
            (await db.KeyTimeToLiveAsync(key)).Should().BeCloseTo(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30));
        }
        finally
        {
            await _redis!.GetDatabase().KeyDeleteAsync(key);
        }
    }

    [Fact]
    public async Task A_library_write_that_stores_nothing_fails_the_check()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider(
            new LegacyEnvelopeProxy(),
            after: services => services.AddKeyedSingleton<ICacheFactory>(RouterServiceCollectionExtensions.StackName, new NullCacheFactory()));

        var report = await RouterConformance.CheckAsync(provider, _cacheName, Samples.Take(1), Ct);

        report.Failures.Should().Contain(f => f.Contains("the library write stored nothing", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stored_null_on_the_library_path_is_a_hit_only_with_CacheNulls(bool cacheNulls)
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider(new LegacyEnvelopeProxy(), cacheNulls);
        var router = provider.GetRequiredService<IRouter>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (router.Mode != RouterMode.Library && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, Ct);
        }

        var key = (RedisKey)$"n:{_cacheName},c:nul";
        await _redis!.GetDatabase().StringSetAsync(key, RedisValue.EmptyString, TimeSpan.FromMinutes(5));
        try
        {
            var runs = 0;
            var value = await router.Get<List<long>>(_cacheName).GetOrAddAsync("nul", _ => { runs++; return Task.FromResult<List<long>?>([7]); }, CacheExpiry.After(TimeSpan.FromMinutes(5)), Ct);

            if (cacheNulls)
            {
                value.Should().BeNull();
                runs.Should().Be(0, "the stored null is a hit");
            }
            else
            {
                runs.Should().Be(1, "without CacheNulls a stored null is a miss");
            }
        }
        finally
        {
            await _redis!.GetDatabase().KeyDeleteAsync(key);
        }
    }

    private ServiceProvider Provider(ISerializerProxy<byte[]> envelope, bool cacheNulls = false, Action<IServiceCollection>? after = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = "cftest",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = fixture.ConnectionString,
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
        }).Build();
        var source = new LegacySource(_redis!, _cacheName);
        var services = new ServiceCollection().AddLogging();
        services.AddCaching(configuration, builder => builder.AddRedisConnection());
        services.AddCachingRouter(configuration, router => router
            .UseLibraryWhen(_ => true)
            .UseEnvelope(envelope)
            .Add(new RouterCacheRegistration<List<long>> { Name = _cacheName, Group = "cfg", Source = source, RedisKey = source.RedisKey, CacheNulls = cacheNulls }));
        after?.Invoke(services);
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IRouter>();
        return provider;
    }

    /// <summary>Writes one byte more than the source's envelope, as a library default leaking into the envelope would.</summary>
    private sealed class DriftingEnvelope : ISerializerProxy<byte[]>
    {
        public byte[]? Serialize(object? value) => value is null ? null : [.. LegacyEnvelope.Write(value), (byte)'x'];

        public T? Deserialize<T>(byte[]? value) => LegacyEnvelope.Read<T>(value);

        public bool TryDeserialize<T>(string? value, out T? result)
        {
            result = default;
            return false;
        }

        public bool TryDeserialize<T>(object? value, out T? result)
        {
            result = default;
            return false;
        }
    }

    /// <summary>A library whose writes report committed and store nothing.</summary>
    private sealed class NullCacheFactory : ICacheFactory
    {
        public IEnumerable<string> ProviderNames => [KnownCacheProviderNames.Redis];

        public ICache CreateCache(string? providerName = null) => NullCache.Instance;

        public IHashCache CreateHashCache(string? providerName = null) => throw new NotSupportedException();

        public void AddProvider(ICacheProvider provider) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
