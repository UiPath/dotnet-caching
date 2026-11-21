using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Migration;

/// <summary>The router entries of the node hash as the leader's adverts; an entry of a router that has left is not an advert.</summary>
internal sealed class RouterAdvertSource(RouterAdvertisementStore store) : IAdvertSource
{
    public async Task<IReadOnlyList<NodeAdvert>> ReadAsync(CancellationToken cancellationToken) =>
        [.. (await store.ReadAsync(cancellationToken).ConfigureAwait(false)).Where(e => !e.Advertisement.Left).Select(e => new NodeAdvert(e.Field, e.Age, e.Advertisement.Tokens))];
}
