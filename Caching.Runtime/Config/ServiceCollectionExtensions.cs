namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class ServiceCollectionExtensions
{
    private const string DefaultSectionName = "Caching";

    public static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration, string sectionName = DefaultSectionName) =>
        services.AddCaching(configuration, opt => configuration.GetSection(sectionName).Bind(opt), sectionName);

    public static IServiceCollection AddCaching(this IServiceCollection services, IConfigurationSection configuration) =>
        services.AddCaching(configuration, opt => configuration.Bind(opt));

    public static IServiceCollection AddCaching(this IServiceCollection services, Action<ICachingBuilder> configure) =>
        services.AddCaching(null, configure);

    public static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration, Action<ICachingBuilder>? configure = null, string sectionName = DefaultSectionName)
    {
        IConfigurationSection section = configuration.GetSection(sectionName);
        return services.AddCaching(section, configure, opt => section.Bind(opt));
    }

    public static IServiceCollection AddCaching(this IServiceCollection services, Action<ICachingBuilder> configure, Action<CacheOptions> configureOptions) =>
        services.AddCaching(null, configure, configureOptions);

    public static IServiceCollection AddCaching(this IServiceCollection services, IConfigurationSection? configuration = null, Action<ICachingBuilder>? configure = null, Action<CacheOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        configuration ??= NullConfigurationSection.Instance;

        services.AddOptions();

        var options = new CacheOptions();
        if(configureOptions != null)
        {
            configureOptions(options);
            services.Configure(configureOptions);
        }

        if (options.Enabled)
        {
            services.TryAddSingleton<ICacheFactory>(sp => new CacheFactory(
                sp.GetRequiredService<IOptions<CacheOptions>>(),
                sp.GetServices<ICacheProvider>(),
                sp.GetService<ICachePolicyFactory>()));
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
        configure?.Invoke(builder);
        builder.Complete();
        return services;
    }

    public static IServiceCollection TryAddMemoryCacheFactory(this IServiceCollection services)
    {
        services.TryAddSingleton<IMemoryCacheFactory>(sp => 
            new MemoryCacheFactory(sp.GetService<ISystemClock>(),
            sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));
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
