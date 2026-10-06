using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>Real multilayer caches over a real <c>MemoryCache</c> and the null inner tier, as the in-memory provider builds them.</summary>
internal static class InMemoryMultilayer
{
    public static MultilayerCache Cache(InMemoryCacheOptions? options = null, ICache? inner = null, bool connectionMonitor = false, TimeProvider? clock = null, ITopicFactory? topics = null, IChangeTokenFactory? tokens = null)
    {
        clock ??= TimeProvider.System;
        options ??= new InMemoryCacheOptions();
        var cacheOptions = new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = connectionMonitor };
        return new MultilayerCache(
            KnownCacheProviderNames.InMemory,
            inner ?? NullCache.Instance,
            new MemoryCacheFactory(clock, NullLoggerFactory.Instance),
            tokens ?? NullChangeTokenFactory.Instance,
            topics ?? NullTopicFactory.Instance,
            NullCacheEventFactory.Instance,
            NullTelemetryProvider.Instance,
            options,
            options,
            cacheOptions,
            localLock: new AsyncKeyedLocalLock(Options.Create(cacheOptions)),
            distributedLock: NullDistributedLock.Instance,
            policyFactory: NullCachePolicyFactory.Instance,
            clock: clock,
            logger: NullLogger.Instance);
    }

    public static MultilayerHashCache HashCache(InMemoryCacheOptions? options = null, IHashCache? inner = null, bool connectionMonitor = false)
    {
        options ??= new InMemoryCacheOptions();
        var cacheOptions = new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = connectionMonitor };
        return new MultilayerHashCache(
            KnownCacheProviderNames.InMemory,
            inner ?? NullHashCache.Instance,
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
