namespace UiPath.Caching.KeyMove;

/// <summary>Verified terminal evidence: the epoch of the committed record, and done with the sweep done when <paramref name="Swept"/>, else off.</summary>
/// <param name="Epoch">The epoch of the committed control record.</param>
/// <param name="Swept">done with the state's <c>lswept</c> stored, else off.</param>
/// <param name="PublishedRelease">For off, the <c>rel</c> of the state when it is DURABLE with the committed record's id; null when the state is not published at that id. A committed control record alone is not publication.</param>
internal readonly record struct ClaimTerminal(long Epoch, bool Swept, string? PublishedRelease = null);
