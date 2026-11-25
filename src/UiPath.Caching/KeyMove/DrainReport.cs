namespace UiPath.Caching.KeyMove;

/// <summary>The confirming pass succeeds only when no keys can move and every skip is accounted for.</summary>
/// <param name="Canary">Keys counted as written after S3 in the last pass.</param>
/// <param name="ExpectedUnmapped">Keys that matched the group's scan and have no owner; never part of the verdict.</param>
internal sealed record DrainReport(bool Passed, IReadOnlyDictionary<string, long> Residue, long Canary, long ExpectedUnmapped = 0);
