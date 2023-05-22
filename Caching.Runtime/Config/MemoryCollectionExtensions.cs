using UiPath.Platform.Caching.Memory;

namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class MemoryCollectionExtensions
{
    private static int _callBackRegistered = 0;

    private const string DefaultSectionName = "MemoryCache";

    public static ICachingBuilder AddMemory(this ICachingBuilder builder, string sectionName = DefaultSectionName) =>
        builder.AddMemory(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    public static ICachingBuilder AddMemory(this ICachingBuilder builder, Action<MemCacheOptions> configure)
    {
        var options = new MemCacheOptions();
        configure(options);
        builder.Services.Configure(configure);
        return builder
            .AddMemoryCache(options, options.IsDefault)
            .AddMemoryRegionCache(options, options.IsDefault);
    }

    public static ICachingBuilder AddMemoryCache(this ICachingBuilder builder, MemCacheOptions? options = null, bool isDefault = false)
    {
        builder.AddCache(sp =>
        {
            var config = options ?? sp.GetService<IOptions<MemCacheOptions>>()?.Value ?? new MemCacheOptions();
            return builder.Enabled && config.Enabled ? sp.BuildMemoryCache(config) : NullCache.Instance;
        });

        builder.Services.TryAddTransient(typeof(IMemCache<>), typeof(MemCache<>));

        if (isDefault)
        {
            builder.Services.TryAddTransient<ICache>(sp => sp.GetRequiredService<IMemCache>());
            builder.Services.TryAddTransient(typeof(ICache<>), typeof(MemCache<>));
        }
        return builder.AddCallback();
    }

    public static ICachingBuilder AddMemoryRegionCache(this ICachingBuilder builder,
        MemCacheOptions? options = null,
        bool isDefault = false,
        Func<IServiceProvider, IChangeTokenFactory?>? changeTokenAccesor = null)
    {
        builder.AddRegionCache(sp =>
        {
            var config = options ?? sp.GetService<IOptions<MemCacheOptions>>()?.Value ?? new MemCacheOptions();
            return builder.Enabled && config.Enabled ? sp.BuildMemoryRegionCache(config) : NullRegionCache.Instance;
        });

        builder.Services.TryAddTransient(typeof(IMemRegionCache<>), typeof(MemRegionCache<>));

        if (isDefault)
        {
            builder.Services.TryAddTransient<IRegionCache>(sp => sp.GetRequiredService<IMemRegionCache>());
            builder.Services.TryAddTransient(typeof(IRegionCache<>), typeof(MemRegionCache<>));
        }
        return builder.AddCallback();
    }

    private static ICachingBuilder AddCallback(this ICachingBuilder builder)
    {
        if (Interlocked.Exchange(ref _callBackRegistered, 1) == 0)
        {
            builder.RegisterOnCompleteCallback(builder =>
            {
                builder.Services.TryAddTransient<Func<MemCacheOptions, IMemoryCache>>(sp => (MemCacheOptions options) =>
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
