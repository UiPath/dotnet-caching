namespace UiPath.Caching.PhaseB;

/// <summary>One master census of the source side of a move.</summary>
/// <param name="Residue">Owned source keys per managed cache, with a zero for a cache that holds none.</param>
/// <param name="Movable">Owned keys a mover pass would still move.</param>
/// <param name="Canary">Owned keys whose life says they were written after S3; null when S3 is unknown, which is no evidence.</param>
internal sealed record CensusResult(IReadOnlyDictionary<string, long> Residue, long Movable, long? Canary);
