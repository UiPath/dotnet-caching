namespace UiPath.Caching.KeyMove;

/// <summary>What one run of a step returned, and the proof of the connection its write went through; no proof when nothing bound it.</summary>
internal readonly record struct ClaimStep(long Code, ConnectionProof? Proof);
