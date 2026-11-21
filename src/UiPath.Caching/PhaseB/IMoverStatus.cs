namespace UiPath.Caching.PhaseB;

/// <summary>What the mover reports to the leader: the drain evaluation and a master census; the mover itself is not part of this change.</summary>
internal interface IMoverStatus
{
    /// <summary>D1, D2 and the canary count; for a reverse, N is the source. Null while the mover has no finished pass.</summary>
    Task<DrainReport?> DrainAsync(string group, string direction, CancellationToken cancellationToken);

    /// <summary>A master census of the residue now, for C7.</summary>
    Task<IReadOnlyDictionary<string, long>> ResidueCensusAsync(string group, CancellationToken cancellationToken);
}
