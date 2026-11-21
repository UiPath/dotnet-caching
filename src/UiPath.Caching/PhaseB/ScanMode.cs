namespace UiPath.Caching.PhaseB;

/// <summary>How a server can be scanned: shard by shard, as one unit behind a proxy, or not at all.</summary>
internal enum ScanMode
{
    None,

    PerShard,

    /// <summary>One logical unit and one cursor, as an enterprise proxy shows a clustered database.</summary>
    ProxySingleUnit,

    /// <summary>The scan answered but the time budget ended before its loop did; not a gate failure, the move's first scan traverses fully.</summary>
    Incomplete,
}
