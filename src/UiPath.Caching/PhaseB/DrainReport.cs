namespace UiPath.Caching.PhaseB;

/// <summary>The mover leader's evaluation of D1 and D2 of the DRAINED criterion, with the census and the canary.</summary>
/// <param name="Passed">D1 and D2 hold: a confirming pass found nothing movable and every skipped key is in an accepted class.</param>
/// <param name="Residue">The residue count per cache, as <c>res:&lt;cache&gt;</c> would store it.</param>
/// <param name="Canary">Keys counted as written after S3 in the last pass.</param>
internal sealed record DrainReport(bool Passed, IReadOnlyDictionary<string, long> Residue, long Canary);
