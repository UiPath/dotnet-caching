namespace UiPath.Caching.KeyMove;

/// <summary>What a destructive data operation of the leader is bound to: the state identity it was decided under and the lease it holds; the operation stops when either is gone.</summary>
internal sealed record DataGuard(string Id, LeaseFence? Fence);
