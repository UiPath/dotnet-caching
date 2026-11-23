namespace UiPath.Caching.KeyMove;

/// <summary>What a publish needs from its capture.</summary>
/// <param name="Px">The source's remaining life at its snapshot read, or -1 for none; for a create, the relative life.</param>
/// <param name="Tcap">The capture time from <see cref="Capture.Tcap"/>, or -1 for a create.</param>
internal readonly record struct Publication(string Token, string GateSeen, string Cid, string NewNonce, long FenceTtlMilliseconds, long Px, long Tcap, bool OnlyIfAbsent);
