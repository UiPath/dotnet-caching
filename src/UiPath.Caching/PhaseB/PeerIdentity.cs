namespace UiPath.Caching.PhaseB;

/// <summary>The state identity a fresh node advertises: what recreation needs from the adverts.</summary>
internal sealed record PeerIdentity(string State, string Direction, long Ep, string Id);
