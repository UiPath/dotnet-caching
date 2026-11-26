namespace UiPath.Caching.KeyMove;

/// <summary>What a destructive data operation of the leader is bound to: the state identity it was decided under and the lease it holds; the operation stops when either is gone.</summary>
/// <param name="AllowNoRecord">A key whose slot holds no control record may be deleted, for a not-started group that no transition ever reached; a record that exists must still hold the identity.</param>
internal sealed record DataGuard(string Id, LeaseFence? Fence, bool AllowNoRecord = false);
