namespace UiPath.Caching.CloudEvents;

[ExcludeFromCodeCoverage]
public static class CachingBuilderExtensions
{
    public static ICachingBuilder AddCloudEvents(this ICachingBuilder builder)
    {
        if(builder.Enabled)
        {
            builder.Services.TryAddSingleton<IEventFormatterProxy<ICacheEvent>>(sp => CacheEventFormatterProxy.CreateDefault());
            builder.Services.TryAddSingleton<ICacheEventFactory, CloudCacheEventFactory>();
        }
        return builder;
    }
}
