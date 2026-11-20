using UiPath.Caching.Redis;

namespace UiPath.Caching;

/// <summary>A shared (distributed) inner tier, Redis or any other cache, with the capabilities it implements.</summary>
internal sealed class SharedInnerTier : InnerTier
{
    public SharedInnerTier(object cache, InMemoryRedisCacheOptions? options = null)
        : base(true, cache as ISharedTier, VersionedFor(cache, options), LimitsFor(options), cache as IConnectionState)
    {
    }

    private static ISharedHashTier? VersionedFor(object cache, InMemoryRedisCacheOptions? options) =>
        options is { PeerFieldUpdates: true } ? cache as ISharedHashTier : null;

    private static PeerFieldLimits LimitsFor(InMemoryRedisCacheOptions? options) =>
        options is null ? default : new PeerFieldLimits(Math.Max(1, options.PeerFieldUpdateMaxFields), Math.Max(1, options.PeerFieldUpdateMaxBytes));
}
