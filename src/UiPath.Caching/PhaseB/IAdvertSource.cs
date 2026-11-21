namespace UiPath.Caching.PhaseB;

/// <summary>Every router entry of the node hash with its age on the server clock; the leader filters the fresh ones.</summary>
internal interface IAdvertSource
{
    Task<IReadOnlyList<NodeAdvert>> ReadAsync(CancellationToken cancellationToken);
}
