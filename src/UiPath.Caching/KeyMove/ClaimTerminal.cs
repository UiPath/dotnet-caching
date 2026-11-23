namespace UiPath.Caching.KeyMove;

/// <summary>Verified terminal evidence: the epoch of the committed record, and S5 with the sweep done when <paramref name="Swept"/>, else S0.</summary>
/// <param name="Epoch">The epoch of the committed control record.</param>
/// <param name="Swept">S5 with the state's <c>lswept</c> stored, else S0.</param>
/// <param name="PublishedRelease">For S0, the <c>rel</c> of the state when it is DURABLE with the committed record's id; null when the state is not published at that id. A committed control record alone is not publication.</param>
internal readonly record struct ClaimTerminal(long Epoch, bool Swept, string? PublishedRelease = null);
