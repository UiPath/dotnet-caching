using CloudNative.CloudEvents.SystemTextJson;

namespace UiPath.Platform.Caching.CloudEvents;

[ExcludeFromCodeCoverage]
public static class CachingBuilderExtensions
{
    public static ICachingBuilder AddCloudEvents(this ICachingBuilder builder)
    {
        if(builder.Enabled)
        {
            builder.Services.TryAddSingleton<IEventFormatterProxy<ICacheEvent>>(sp => new CacheEventFormatterProxy(new JsonEventFormatter<CacheEventData>()));
            builder.Services.TryAddSingleton<ICacheEventFactory, CloudCacheEventFactory>();
        }
        return builder;
    }
}
