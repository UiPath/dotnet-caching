namespace UiPath.Caching.KeyMove;

/// <summary>An unfinished acquisition and whether its Src pend may still apply.</summary>
internal sealed record AcquisitionToken(string Token, bool SrcUncertain);
