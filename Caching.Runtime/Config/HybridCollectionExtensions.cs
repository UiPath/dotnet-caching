using System.Diagnostics.CodeAnalysis;
using CloudNative.CloudEvents.SystemTextJson;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Platform.Caching.Hybrid;
using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class HybridCollectionExtensions
{
    private static bool CallBackRegistered = false;

    public static ICachingBuilder AddHybridCache(this ICachingBuilder builder, Func<IServiceProvider, ICache> innerCache, HybridCacheOptions? options = null, bool isDefault = false)
    {
        if (builder.Enabled)
        {
            builder.AddCache(sp => sp.BuildHybridCache(innerCache, options));
        }
        else
        {
            builder.AddCache(sp => (IHybridCache)NullCache.Instance);
        }

        builder.Services.TryAddTransient(typeof(IHybridCache<>), typeof(HybridCache<>));

        if (isDefault)
        {
            builder.Services.TryAddTransient<ICache>(sp => sp.GetRequiredService<IHybridCache>());
            builder.Services.TryAddTransient(typeof(ICache<>), typeof(HybridCache<>));
        }
        return builder.AddCallback();
    }

    public static ICachingBuilder AddHybridRegionCache(this ICachingBuilder builder, Func<IServiceProvider, IRegionCache> innerCache, HybridCacheOptions? options = null, bool isDefault = false)
    {
        if (builder.Enabled)
        {
            builder.AddRegionCache(sp => sp.BuildHybridRegionCache(innerCache, options));
        }
        else
        {
            builder.AddRegionCache(sp => (IHybridRegionCache)NullRegionCache.Instance);
        }

        builder.Services.TryAddTransient(typeof(IHybridRegionCache<>), typeof(HybridRegionCache<>));

        if (isDefault)
        {
            builder.Services.TryAddTransient<IRegionCache>(sp => sp.GetRequiredService<IHybridRegionCache>());
            builder.Services.TryAddTransient(typeof(IRegionCache<>), typeof(HybridRegionCache<>));
        }
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
        if (!CallBackRegistered)
        {
            builder.RegisterOnCompleteCallback(builder =>
            {
                builder.Services.TryAddSingleton<IChannelResolver, DefaultChannelResolver>();
                builder.Services.TryAddSingleton<IChangeTokenFactory, ChangeTokenFactory>();
                builder.Services.TryAddSingleton<CloudEventFormatter>(new JsonEventFormatter<ClearCacheEventData>());
                builder.Services.TryAddTransient(sp => sp.BuildRedisChannelSubscriber());
                builder.Services.TryAddTransient(sp => sp.BuildRedisChannelPublisher());
                builder.Services.TryAddTransient(sp => sp.GetRequiredService<IRedisConnection>().Connection.GetSubscriber());

                builder.Services.TryAddTransient<Func<IMemoryCache>>(sp => () =>
                     new MemoryCache(
                        Options.Create(new MemoryCacheOptions
                        {
                            Clock = sp.GetService<ISystemClock>()
                        }),
                        sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));
            });
            CallBackRegistered = true;
        }

        return builder;
    }
}
