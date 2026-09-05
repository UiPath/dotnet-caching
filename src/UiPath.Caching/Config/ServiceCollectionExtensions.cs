namespace UiPath.Caching.Config;

[ExcludeFromCodeCoverage]
public static class ServiceCollectionExtensions
{
    private const string DefaultSectionName = "Caching";

    public static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration, string sectionName = DefaultSectionName) =>
        services.AddCaching(configuration, configure: null, sectionName);

    /// <summary>
    /// Binds <see cref="CacheOptions"/> from <paramref name="configuration"/>. The binder is passed by name:
    /// positionally it lands on the <c>configure</c> parameter instead, where it type-checks as
    /// <c>Action&lt;ICachingBuilder&gt;</c> and leaves the section unread.
    /// </summary>
    public static IServiceCollection AddCaching(this IServiceCollection services, IConfigurationSection configuration) =>
        services.AddCaching(configuration, configure: null, configureOptions: opt => configuration.Bind(opt));

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

        SeedDefaultKeyCasing(options);

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

        services.TryAddCacheClock();
        var builder = new CachingBuilder(services, configuration)
        {
            Enabled = options.Enabled
        };
        configure?.Invoke(builder);
        builder.Complete();
        return services;
    }

    /// <summary>
    /// The library's one clock. Built over the <see cref="ISystemClock"/> the container registered when
    /// there is one, else the system clock; register either before <c>AddCaching</c> to control time.
    /// </summary>
    public static IServiceCollection TryAddCacheClock(this IServiceCollection services)
    {
        services.TryAddSingleton<ICacheClock>(sp => new CacheClock(sp.GetService<ISystemClock>()));
        return services;
    }

    public static IServiceCollection TryAddMemoryCacheFactory(this IServiceCollection services)
    {
        services.TryAddCacheClock();
        services.TryAddSingleton<IMemoryCacheFactory>(sp =>
            new MemoryCacheFactory(sp.GetRequiredService<ICacheClock>(),
            sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));
        return services;
    }

    /// <summary>
    /// Validates the casing and seeds <see cref="CacheKey.DefaultCasing"/>. Called eagerly from
    /// <c>AddCaching</c>, because a key built before anything resolves <see cref="IOptions{CacheOptions}"/>
    /// would normalize with the wrong casing, and again from the PostConfigure callback, which is what
    /// options registered after <c>AddCaching</c> reach. Both paths validate: configuration binding accepts
    /// an out-of-range enum numerically, and assigning one straight to the global default would be reported
    /// only by whichever key happened to be built next.
    /// </summary>
    internal static void SeedDefaultKeyCasing(CacheOptions options)
    {
        if (options.KeyCasing is not (CacheKeyCasing.Insensitive or CacheKeyCasing.Sensitive))
        {
            throw new InvalidOperationException(
                $"CacheOptions.KeyCasing has the unsupported value {(int)options.KeyCasing}. Use {nameof(CacheKeyCasing.Insensitive)} or {nameof(CacheKeyCasing.Sensitive)}.");
        }

        CacheKey.DefaultCasing = options.KeyCasing;
    }

    public static IServiceCollection TryConfigure<TOptions>(this IServiceCollection services, Action<TOptions> configureOptions)
         where TOptions : class
    {
        if (!services.Any(d => d.ServiceType == typeof(IConfigureOptions<TOptions>)))
        {
            services.Configure(configureOptions);
        }
        return services;
    }
}
