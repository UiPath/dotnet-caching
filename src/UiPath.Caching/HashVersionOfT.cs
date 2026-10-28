namespace UiPath.Caching;

internal sealed class HashVersion<T>(long number, OwnWrite[] own, DateTimeOffset? localDeadline) : HashVersion(number, own, localDeadline)
{
    internal override bool Apply(MultilayerHashCache cache, RefreshMetadataState state, object current, PeerUpdate update) =>
        cache.ApplyPeerUpdate<T>(state, (ICacheEntry<IDictionary<string, T?>>)current, this, update);
}
