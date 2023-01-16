using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCaching(this IServiceCollection services, Action<ICachingBuilder> configureCache, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(configureCache);
        var builder = new CachingBuilder(services, enabled);
        configureCache.Invoke(builder);
        builder.Complete();
        return services;
    }
}
