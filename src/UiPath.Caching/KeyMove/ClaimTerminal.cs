namespace UiPath.Caching.KeyMove;

/// <summary>Verified terminal evidence: the epoch of the committed record, and S5 with the sweep done when <paramref name="Swept"/>, else S0.</summary>
internal readonly record struct ClaimTerminal(long Epoch, bool Swept);
