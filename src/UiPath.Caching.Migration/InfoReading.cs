namespace UiPath.Caching.Migration;

/// <summary>One reading of every node: their load, and the pause the memory rules ask for, if any.</summary>
/// <param name="Loads">The nodes' CPU and replication state.</param>
/// <param name="MemoryPause">The reason of the memory rule that pauses the mover (<c>noeviction</c>, <c>memory_ceiling</c>, <c>evicted</c> or <c>memory_growth</c>); null when none does.</param>
internal sealed record InfoReading(IReadOnlyList<NodeLoad> Loads, string? MemoryPause);
