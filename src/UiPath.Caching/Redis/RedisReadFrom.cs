namespace UiPath.Caching.Redis;

/// <summary>Which node answers a read.</summary>
public enum RedisReadFrom
{
    /// <summary>The master (<c>PreferMaster</c>), which answers in about a third of a replica's time and cannot return a key it has already removed; a replica answers only while no master is reachable.</summary>
    Master = 0,

    /// <summary>A replica when there is one, except for a key this node wrote or heard invalidated through a local entry within <see cref="RedisCacheOptions.MasterReadWindow"/>, which still reads from the master.</summary>
    Replica = 1,
}
