namespace UiPath.Caching.KeyMove;

/// <summary>One master inventory of the source side of a move.</summary>
/// <param name="Residue">Owned source keys per managed cache, with a zero for a cache that holds none.</param>
/// <param name="Movable">Owned keys a mover pass would still move.</param>
/// <param name="Droppable">Owned keys whose value the codec refuses for good; they stay in the residue, are not movable, and a delete removes them.</param>
/// <param name="Ambiguous">Keys that more than one cache owns; each is counted in the residue of every cache that claims it, never moved, and blocks the confirming pass.</param>
/// <param name="InvalidMapping">Owned keys whose registration maps them outside the namespace or onto a reserved key; a defect that no mover pass can finish, so the drain evidence fails while any exists.</param>
/// <param name="Canary">Owned keys whose life says they were written after S3; null when S3 is unknown, which is no evidence.</param>
internal sealed record InventoryResult(IReadOnlyDictionary<string, long> Residue, long Movable, long? Canary, long Ambiguous = 0, long InvalidMapping = 0, long Droppable = 0);
