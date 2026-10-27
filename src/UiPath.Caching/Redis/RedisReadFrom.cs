namespace UiPath.Caching.Redis;

/// <summary>Which node answers a read.</summary>
public enum RedisReadFrom
{
    /// <summary>The master (<c>PreferMaster</c>), which answers faster and cannot return a key it has already removed; a replica answers only while no master is reachable.</summary>
    Master = 0,

    /// <summary>A replica when there is one, which can lag behind a write or an invalidation.</summary>
    Replica = 1,
}
