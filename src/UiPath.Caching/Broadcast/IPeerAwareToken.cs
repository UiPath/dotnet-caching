namespace UiPath.Caching.Broadcast;

internal interface IPeerAwareToken
{
    /// <summary>Told of the fields a peer's event wrote, before the token changes; true when the entry was updated and the token need not change.</summary>
    Func<PeerUpdate, bool>? PeerUpdateHandler { get; set; }

    /// <summary>The one callback told, before the token changes, that an event or a loss is changing it; the flag is true for a loss, which names no key.</summary>
    void RegisterInvalidatingCallback(Action<object?, bool> callback, object? state);

    /// <summary>The one hook offered a peer's write before the token changes; true when it took the write and the token need not change.</summary>
    void RegisterPeerRefresh(Func<object?, bool> refresh, object? state);
}
