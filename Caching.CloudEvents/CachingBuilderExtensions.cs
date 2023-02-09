using System.Diagnostics.CodeAnalysis;
using CloudNative.CloudEvents.SystemTextJson;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UiPath.Platform.Caching.Broadcast;
using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.CloudEvents;

[ExcludeFromCodeCoverage]
public static class CachingBuilderExtensions
{
    public static ICachingBuilder AddCloudEvents(this ICachingBuilder builder)
    {
        builder.Services.TryAddSingleton<IEventFormatterProxy<IClearCacheEvent>>(sp => new CacheClearEventFormatterProxy(new JsonEventFormatter<ClearCacheEventData>()));
        builder.Services.TryAddSingleton<IClearCacheEventFactory, ClearCacheEventFactory>();
        return builder;
    }
}
