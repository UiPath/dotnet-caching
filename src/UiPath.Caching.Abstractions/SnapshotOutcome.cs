namespace UiPath.Caching;

/// <summary>What <see cref="IMigrationCache.ReadSnapshotAsync{T}"/> found.</summary>
public enum SnapshotOutcome
{
    /// <summary>The store could not answer; <c>default</c>, so an unconfigured mock never reads as a miss.</summary>
    Failed = 0,

    Absent,

    /// <summary>The key holds a value, which is <see cref="SnapshotRead{T}.Entry"/>.</summary>
    Found,

    /// <summary>The key holds a value this cache cannot read; it was left in place.</summary>
    Unreadable,
}
