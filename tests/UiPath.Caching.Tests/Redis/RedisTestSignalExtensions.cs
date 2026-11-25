using Microsoft.Extensions.DependencyInjection;

namespace UiPath.Caching.Tests.Redis;

internal static class RedisTestSignalExtensions
{
    public static IServiceCollection ObserveRedisSignals(this IServiceCollection services, RedisTestSignals signals) => signals.Observe(services);
}
