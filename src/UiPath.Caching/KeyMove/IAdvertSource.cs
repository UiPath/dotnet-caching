namespace UiPath.Caching.KeyMove;

internal interface IAdvertSource
{
    Task<IReadOnlyList<NodeAdvert>> ReadAsync(CancellationToken cancellationToken);
}
