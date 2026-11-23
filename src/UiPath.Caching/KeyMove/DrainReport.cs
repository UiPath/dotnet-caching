namespace UiPath.Caching.KeyMove;

/// <summary>The confirming pass succeeds only when no keys can move and every skip is accounted for.</summary>
/// <param name="Canary">Keys counted as written after S3 in the last pass.</param>
internal sealed record DrainReport(bool Passed, IReadOnlyDictionary<string, long> Residue, long Canary);
