namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class ServiceCollectionExtensions
{
    private const string DefaultSectionName = "Caching";

    public static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration, Action<ICachingBuilder> configureCache, string sectionName = DefaultSectionName)
    {
        ArgumentNullException.ThrowIfNull(configureCache);
        var section = configuration.GetSection(sectionName);
        var builder = new CachingBuilder(services, section)
        {
            Enabled = section.GetValue<bool?>("Enabled").GetValueOrDefault(true)
        };
        configureCache.Invoke(builder);
        builder.Complete();
        return services;
    }
}
