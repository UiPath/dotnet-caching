namespace UiPath.Caching.KeyMove;

internal interface IMoverStatus
{
    /// <summary>Returns null until a pass finishes, using native keys as the source during reversal.</summary>
    Task<DrainReport?> DrainAsync(string group, StateRecord state, CancellationToken cancellationToken);

    /// <summary>Reads current residue from the masters.</summary>
    Task<IReadOnlyDictionary<string, long>> ResidueInventoryAsync(string group, CancellationToken cancellationToken);
}
