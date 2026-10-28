namespace UiPath.Caching.Broadcast;

internal interface IPeerUpdateSource
{
    /// <summary>Told of the fields a peer's event wrote, before the token changes; true when the entry was updated and the token need not change.</summary>
    Func<PeerUpdate, bool>? PeerUpdateHandler { get; set; }
}
