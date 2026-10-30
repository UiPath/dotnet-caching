namespace UiPath.Caching.Broadcast;

internal interface IPeerRefreshSource
{
    /// <summary>Registers the one hook offered a peer's write before the token changes; true when it took the write and the token need not change.</summary>
    void RegisterPeerRefresh(Func<object?, bool> refresh, object? state);
}
