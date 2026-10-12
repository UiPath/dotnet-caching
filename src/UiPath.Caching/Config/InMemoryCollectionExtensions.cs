namespace UiPath.Caching.Config;

[ExcludeFromCodeCoverage]
public static class InMemoryCollectionExtensions
{
    public static ICachingBuilder AddMemory(this ICachingBuilder builder, string sectionName = KnownCacheProviderNames.InMemory)
    {
        var section = builder.Configuration.GetSection(sectionName);
        RemovedConfigurationKeys.ThrowIfPresent(section, RemovedConfigurationKeys.Multilayer);
        return builder.AddMemory(opt => section.Bind(opt));
    }

    public static ICachingBuilder AddMemory(this ICachingBuilder builder, Action<InMemoryCacheOptions> configure)
    {
        var options = new InMemoryCacheOptions();
        configure(options);
        builder.Services.Configure(configure);
        if (!builder.Enabled || !options.Enabled)
        {
            return builder;
        }
        var validation = new MultilayerCacheLockOptionsValidator<InMemoryCacheOptions>().Validate(name: null, options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                nameof(InMemoryCacheOptions),
                typeof(InMemoryCacheOptions),
                validation.Failures);
        }
        builder.Services.TryAddMemoryCacheFactory();
        builder.Services
            .TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<InMemoryCacheOptions>, MultilayerCacheLockOptionsValidator<InMemoryCacheOptions>>());
        // The policy validator checks lock timeouts against these intervals, so they are checked at start even with no distributed lock registered.
        builder.Services
            .TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CacheOptions>, CacheOptionsLockValidator>());
        builder.Services
            .TryAddEnumerable(ServiceDescriptor.Singleton<ICacheProvider, InMemoryCacheProvider>());

        return builder;
    }
}
