namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class HybridCollectionExtensions
{
    private static int _callBackRegistered = 0;
    private const string DefaultSectionName = "HybridCache";

    public static ICachingBuilder AddHybrid(this ICachingBuilder builder,
        string sectionName = DefaultSectionName,
        Func<IServiceProvider, ICache>? innerCache = null,
        Func<IServiceProvider, IRegionCache>? innerRegionCache = null) =>
    builder.AddHybrid(opt => builder.Configuration.GetSection(sectionName).Bind(opt), innerCache, innerRegionCache);

    public static ICachingBuilder AddHybrid(this ICachingBuilder builder,
    Action<HybridCacheOptions> configure,
    Func<IServiceProvider, ICache>? innerCache = null,
    Func<IServiceProvider, IRegionCache>? innerRegionCache = null)
    {
        var options = new HybridCacheOptions();
        configure(options);
        builder.Services.Configure(configure);
        if (options.Enabled)
        {
            builder.ConfigureBroadcast(opt =>
            {
                opt.ChannelPrefix = options?.Broadcast?.ChannelPrefix ?? "cache";
                opt.SourceUri = options?.Broadcast?.SourceUri;
            });
        }
        return builder
            .AddHybridCache(innerCache, options, options.IsDefault)
            .AddHybridRegionCache(innerRegionCache, options, options.IsDefault);
    }

    public static ICachingBuilder AddHybridCache(this ICachingBuilder builder, Func<IServiceProvider, ICache>? innerCache = null, HybridCacheOptions? options = null, bool isDefault = false)
    {
        builder.AddCache(sp =>
        {
            var config = options ?? sp.GetService<IOptions<HybridCacheOptions>>()?.Value ?? new HybridCacheOptions();
            return builder.Enabled && config.Enabled ? sp.BuildHybridCache(innerCache, config) : NullCache.Instance;
        });

        builder.Services.TryAddTransient(typeof(IHybridCache<>), typeof(HybridCache<>));

        if (isDefault)
        {
            builder.Services.TryAddTransient<ICache>(sp => sp.GetRequiredService<IHybridCache>());
            builder.Services.TryAddTransient(typeof(ICache<>), typeof(HybridCache<>));
        }
        return builder.AddCallback();
    }

    public static ICachingBuilder AddHybridRegionCache(this ICachingBuilder builder, Func<IServiceProvider, IRegionCache>? innerCache = null, HybridCacheOptions? options = null, bool isDefault = false)
    {
        builder.AddRegionCache(sp =>
        {
            var config = options ?? sp.GetService<IOptions<HybridCacheOptions>>()?.Value ?? new HybridCacheOptions();
            return builder.Enabled && config.Enabled ? sp.BuildHybridRegionCache(innerCache, config) : NullRegionCache.Instance;
        });

        builder.Services.TryAddTransient(typeof(IHybridRegionCache<>), typeof(HybridRegionCache<>));

        if (isDefault)
        {
            builder.Services.TryAddTransient<IRegionCache>(sp => sp.GetRequiredService<IHybridRegionCache>());
            builder.Services.TryAddTransient(typeof(IRegionCache<>), typeof(HybridRegionCache<>));
        }
        return builder.AddCallback();
    }

    public static ICachingBuilder ConfigureBroadcast(this ICachingBuilder builder, Action<BroadcastOptions> configureOptions)
    {
        BroadcastOptions options = new();
        configureOptions.Invoke(options);
        builder.Services.Configure(configureOptions);
        return builder.AddCallback();
    }

    public static ICachingBuilder ConfigureHybrid(this ICachingBuilder builder, Action<HybridCacheOptions> configureOptions)
    {
        HybridCacheOptions options = new();
        configureOptions.Invoke(options);
        builder.Services.Configure(configureOptions);
        return builder.AddCallback();
    }

    private static ICachingBuilder AddCallback(this ICachingBuilder builder)
    {
        if (Interlocked.Exchange(ref _callBackRegistered, 1) == 0)
        {
            builder.RegisterOnCompleteCallback(builder =>
            {
                builder.Services.TryAddSingleton<IChannelResolver, ChannelResolver>();
                builder.Services.TryAddSingleton<IChangeTokenFactory, ChangeTokenFactory>();
                builder.Services.TryAddSingleton<IEventFormatterProxy<ICacheEvent>, CacheEventFormatter>();
                builder.Services.TryAddSingleton<ICacheEventFactory, CacheEventFactory>();
                builder.Services.TryAddTransient(sp => sp.BuildRedisChannelSubscriber<ICacheEvent>());
                builder.Services.TryAddTransient(sp => sp.BuildRedisChannelPublisher<ICacheEvent>());
                builder.Services.TryAddTransient(sp => sp.GetRequiredService<IRedisConnection>().Connection.GetSubscriber());

                builder.Services.TryAddTransient<Func<HybridCacheOptions, IMemoryCache>>(sp => (HybridCacheOptions options) =>
                     new MemoryCache(
                        Options.Create(new MemoryCacheOptions
                        {
                            Clock = sp.GetService<ISystemClock>(),
                            TrackStatistics = options.TrackStatistics
                        }),
                        sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));
            });
        }

        return builder;
    }
}
