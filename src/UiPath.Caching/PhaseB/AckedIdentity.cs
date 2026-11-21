namespace UiPath.Caching.PhaseB;

internal sealed record AckedIdentity(long Ep, string Id, string State, string Direction);
