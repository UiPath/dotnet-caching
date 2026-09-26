using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>Real multilayer caches over a real <c>MemoryCache</c> and the null inner tier, as the in-memory provider builds them.</summary>
internal static class InMemoryMultilayer
{
    public static MultilayerCache Cache(InMemoryCacheOptions? options = null)
    {
        options ??= new InMemoryCacheOptions();
        var cacheOptions = new CacheOptions { AppShortName = "test" };
        return new MultilayerCache(
            KnownCacheProviderNames.InMemory,
            NullCache.Instance,
            new MemoryCacheFactory(TimeProvider.System, NullLoggerFactory.Instance),
            NullChangeTokenFactory.Instance,
            NullTopicFactory.Instance,
            NullCacheEventFactory.Instance,
            NullTelemetryProvider.Instance,
            options,
            options,
            cacheOptions,
            localLock: new AsyncKeyedLocalLock(Options.Create(cacheOptions)),
            distributedLock: NullDistributedLock.Instance,
            policyFactory: NullCachePolicyFactory.Instance,
            clock: TimeProvider.System,
            logger: NullLogger.Instance);
    }

    public static MultilayerHashCache HashCache(InMemoryCacheOptions? options = null)
    {
        options ??= new InMemoryCacheOptions();
        var cacheOptions = new CacheOptions { AppShortName = "test" };
        return new MultilayerHashCache(
            KnownCacheProviderNames.InMemory,
            NullHashCache.Instance,
            new MemoryCacheFactory(TimeProvider.System, NullLoggerFactory.Instance),
            NullChangeTokenFactory.Instance,
            NullTopicFactory.Instance,
            NullCacheEventFactory.Instance,
            NullTelemetryProvider.Instance,
            options,
            options,
            cacheOptions,
            localLock: new AsyncKeyedLocalLock(Options.Create(cacheOptions)),
            distributedLock: NullDistributedLock.Instance,
            policyFactory: NullCachePolicyFactory.Instance,
            clock: TimeProvider.System,
            logger: NullLogger.Instance);
    }
}
