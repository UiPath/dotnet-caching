namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class InMemoryCollectionExtensions
{
    public static ICachingBuilder AddMemory(this ICachingBuilder builder, string sectionName = KnownCacheProviderNames.InMemory) =>
        builder.AddMemory(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

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
        builder.AddLocalLock();
        builder.Services
            .TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<InMemoryCacheOptions>, MultilayerCacheLockOptionsValidator<InMemoryCacheOptions>>());
        builder.Services
            .TryAddEnumerable(ServiceDescriptor.Singleton<ICacheProvider, InMemoryCacheProvider>());

        return builder;
    }
}
