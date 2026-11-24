namespace UiPath.Caching.KeyMove;

/// <summary>The condition of a retried Dst-side hash write: it lands only while the fence holds what the first attempt set or the token seen just before it.</summary>
/// <param name="Observed">The token seen just before the first attempt, or null when none was seen, so that only the first attempt's own nonce can match.</param>
internal readonly record struct FenceRetry(string? Observed);
