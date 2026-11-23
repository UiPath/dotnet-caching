namespace UiPath.Caching.KeyMove;

internal sealed record AckedIdentity(long Ep, string Id, string State, string Direction);
