using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed class RouterAdvertSource(RouterAdvertisementStore store) : IAdvertSource
{
    public async Task<IReadOnlyList<NodeAdvert>> ReadAsync(CancellationToken cancellationToken) =>
        [.. (await store.ReadAsync(cancellationToken).ConfigureAwait(false)).Where(e => !e.Advertisement.Left).Select(e => new NodeAdvert(e.Field, e.Age, e.Advertisement.Tokens))];
}
