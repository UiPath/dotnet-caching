using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// UseKeyMove without a usable Redis cache, in the application or in the router, fails the host once at startup; a configuration that needs none starts.
public sealed class KeyMoveStartupTests
{
    public enum App
    {
        NoRedisProvider,
        DefaultCacheIsNotRedis,
        RedisProviderWithoutCodec,
        InMemoryDefaultWithRedis,
        NoKeyMove,
        ZeroCaches,
    }

    [Theory]
    [InlineData(App.NoRedisProvider, "application's Redis cache")]
    [InlineData(App.DefaultCacheIsNotRedis, "application's Redis cache")]
    [InlineData(App.RedisProviderWithoutCodec, "application's Redis cache")]
    public async Task Startup_fails_with_a_clear_message_each_time_it_is_tried(App app, string message)
    {
        await using var provider = Provider(app);

        var act = () => provider.GetRequiredService<IRouter>();

        act.Should().Throw<InvalidOperationException>().WithMessage($"*UseKeyMove needs the {message}*");
        act.Should().Throw<InvalidOperationException>("a second attempt fails the same way, and nothing is retried in the background");
    }

    [Theory]
    [InlineData(App.InMemoryDefaultWithRedis)]
    [InlineData(App.NoKeyMove)]
    public async Task A_configuration_that_needs_no_other_cache_starts(App app)
    {
        await using var provider = Provider(app);

        var act = () => provider.GetRequiredService<IRouter>();

        act.Should().NotThrow();
    }

    // A key move with no cache has nothing to move, and the registration refuses it before any host starts.
    [Fact]
    public void UseKeyMove_with_a_group_and_no_cache_is_refused_at_registration()
    {
        var act = () => Provider(App.ZeroCaches);

        act.Should().Throw<InvalidOperationException>().WithMessage("*key move group 'g' has no cache*");
    }

    // The router's own stack always registers its Redis provider (its section cannot switch it off), so the check on that side is proved on the factories.
    [Fact]
    public async Task Each_side_of_the_check_names_the_cache_it_misses()
    {
        await using var working = Provider(App.InMemoryDefaultWithRedis);
        var factory = working.GetRequiredService<ICacheFactory>();

        var noRouter = () => RouterServiceCollectionExtensions.ValidateCodecCaches(factory, NullCacheFactory.Instance);
        var noApp = () => RouterServiceCollectionExtensions.ValidateCodecCaches(NullCacheFactory.Instance, factory);
        var both = () => RouterServiceCollectionExtensions.ValidateCodecCaches(factory, factory);

        noRouter.Should().Throw<InvalidOperationException>().WithMessage("*router's own Redis cache*");
        noApp.Should().Throw<InvalidOperationException>().WithMessage("*application's Redis cache*");
        both.Should().NotThrow();
    }

    private static ServiceProvider Provider(App app)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = "kmstart",
            ["Caching:DefaultCache"] = UsesMemoryDefault(app) ? KnownCacheProviderNames.InMemory : KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = "127.0.0.1:1,abortConnect=false",
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
            ["RouterCaching:AppShortName"] = "kmstartrouter",
            ["RouterCaching:Separator"] = "$",
            ["RouterCaching:SourceUri"] = "urn:km-start",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddCaching(configuration, builder => AddApplicationCaching(builder, app));
        if (app == App.RedisProviderWithoutCodec)
        {
            services.AddSingleton<ICacheProvider>(new NullRedisProvider());
        }

        services.AddCachingRouter(configuration, router => ConfigureRouter(router, app), "RouterCaching");
        return services.BuildServiceProvider();
    }

    private static bool UsesMemoryDefault(App app) => app is App.DefaultCacheIsNotRedis or App.InMemoryDefaultWithRedis;

    private static void AddApplicationCaching(ICachingBuilder builder, App app)
    {
        builder.AddRedisConnection();
        if (UsesMemoryDefault(app))
        {
            builder.AddMemory();
        }

        if (app == App.InMemoryDefaultWithRedis)
        {
            builder.AddRedis();
        }
    }

    private static void ConfigureRouter(RouterBuilder router, App app)
    {
        router
            .UseLibraryWhen(_ => true)
            .UseEnvelope(new LegacyEnvelopeProxy())
            .UseSourceConnection(() => throw new NotSupportedException())
            .Add(new RouterCacheRegistration<Item> { Name = "items", Group = app == App.ZeroCaches ? "other" : "g", Source = new NoSource(), RedisKey = key => $"l:{key}" });
        if (app == App.NoKeyMove)
        {
            return;
        }

        router.UseKeyMove(new Stage(), moves =>
        {
            moves.UseLibraryKeyPrefix("kmstart:").AddGroup("g");
            if (app != App.ZeroCaches)
            {
                moves.AddCache(ItemsRegistration());
            }
        });
    }

    private static KeyMoveCacheRegistration ItemsRegistration() => new()
    {
        Name = "items",
        Group = "g",
        InPlacePattern = "l:*",
        ParseInPlaceKey = key => key.StartsWith("l:", StringComparison.Ordinal) ? key[2..] : null,
        NativePattern = "kmstart:s:*",
        FormatNativeKey = key => $"kmstart:s:{key}",
        ParseNativeKey = key => key.StartsWith("kmstart:s:", StringComparison.Ordinal) ? key[10..] : null,
    };

    private sealed class Stage : IMigrationStageSource
    {
        public ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken) => ValueTask.FromResult<int?>(2);
    }

    // A provider named like the Redis one whose cache cannot encode: it is not a Redis cache.
    private sealed class NullRedisProvider : ICacheProvider
    {
        public string Name => KnownCacheProviderNames.Redis;

        public bool Enabled => true;

        public ICache CreateCache() => NullCache.Instance;

        public IHashCache CreateHashCache() => NullHashCache.Instance;

        public void Dispose()
        {
        }
    }

    private sealed class NoSource : ISourceCache<Item>
    {
        public ValueTask<SourceRead<Item>> GetAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<WriteResult> SetAsync(string key, Item value, CacheExpiry expiry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<AddResult> TryAddAsync(string key, Item value, CacheExpiry expiry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
