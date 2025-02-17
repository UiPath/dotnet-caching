namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class InMemoryCollectionExtensions
{
    private static int _callbackRegistered = 0;
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
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ICacheProvider, InMemoryCacheProvider>());

        return builder.AddCallback();
    }

    private static ICachingBuilder AddCallback(this ICachingBuilder builder)
    {
        if (Interlocked.Exchange(ref _callbackRegistered, 1) == 0)
        {
            builder.RegisterOnCompleteCallback(builder =>
            {
                builder.Services.AddMemoryCacheFactory();
            });
        }

        return builder;
    }
}
