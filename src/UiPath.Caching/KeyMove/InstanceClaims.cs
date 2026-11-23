namespace UiPath.Caching.KeyMove;

internal sealed record InstanceClaims(ClaimValue? Src, ClaimValue? Mirror, bool SrcMalformed, bool MirrorKnown, bool MirrorMalformed);
