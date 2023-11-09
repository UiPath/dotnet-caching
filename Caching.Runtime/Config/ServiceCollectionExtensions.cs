namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class ServiceCollectionExtensions
{
    private const string DefaultSectionName = "Caching";

    public static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration, string sectionName = DefaultSectionName)
    {
        return services.AddCaching(configuration, opt => configuration.GetSection(sectionName).Bind(opt), sectionName);
    }

    public static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration, Action<ICachingBuilder> configure, string sectionName = DefaultSectionName)
    {
        IConfigurationSection section = configuration.GetSection(sectionName);
        return services.AddCaching(section, configure, opt => section.Bind(opt));
    }

    public static IServiceCollection AddCaching(this IServiceCollection services, IConfigurationSection configuration, Action<ICachingBuilder> configure, Action<CacheOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions();

        var options = new CacheOptions();
        configureOptions(options);
        services.Configure(configureOptions);
        
        if (options.Enabled)
        {
            services.TryAddSingleton(typeof(ICacheFactory), options.CacheFactory ?? typeof(CacheFactory));
        }
        else
        {
            services.TryAddSingleton<ICacheFactory, NullCacheFactory>();
        }

        services.TryAddTransient<Func<ICacheFactory>>(ctx => () => ctx.GetRequiredService<ICacheFactory>());
        services.TryAddTransient(typeof(ICache<>), typeof(Cache<>));
        services.TryAddTransient(typeof(IHashCache<>), typeof(HashCache<>));

        var builder = new CachingBuilder(services, configuration)
        {
            Enabled = options.Enabled
        };
        configure.Invoke(builder);
        builder.Complete();
        return services;
    }

    public static IServiceCollection AddMemoryCacheFactory(this IServiceCollection services)
    {
        services.TryAddSingleton<IMemoryCacheFactory>(sp => new MemoryCacheFactory(sp.GetService<ISystemClock>(), sp.GetService<ILoggerFactory>()));
        return services;
    }

    internal static IServiceCollection TryConfigure<TOptions>(this IServiceCollection services, Action<TOptions> configureOptions)
         where TOptions : class
    {
        if (!services.Any(d => d.ServiceType == typeof(IConfigureOptions<TOptions>)))
        {
            services.Configure(configureOptions);
        }
        return services;
    }
}
