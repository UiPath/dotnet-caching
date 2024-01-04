using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Config;

internal static class NullInstanceExtensions
{
    internal static void TryAddNullInstances(this IServiceCollection services)
    {
        services.TryAddSingleton<IChangeTokenFactory>(NullChangeTokenFactory.Instance);
        services.TryAddSingleton<ITopicFactory>(NullTopicFactory.Instance);
        services.TryAddSingleton<ICachingTelemetryProvider>(NullTelemetryProvider.Instance);
        services.TryAddSingleton<ICacheEventFactory>(NullCacheEventFactory.Instance);
        services.TryAddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
    }
}
