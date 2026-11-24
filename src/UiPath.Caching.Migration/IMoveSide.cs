using StackExchange.Redis;

namespace UiPath.Caching.Migration;

/// <summary>Maps caller keys to source Redis commands while the client owns scheduling.</summary>
internal interface IMoveSide<T>
    where T : class
{
    IKeyMoveCodec<T> Codec { get; }

    bool IsAvailable { get; }

    string Key(string logical);

    /// <summary>Only keys whose mapping round trips can be promoted.</summary>
    bool Maps(string logical);

    /// <summary>Reads bytes and remaining life together on the master.</summary>
    ValueTask<KeyMoveSnap> SnapshotAsync(string logical, CancellationToken cancellationToken);

    /// <returns>Null when the probe could not be made.</returns>
    ValueTask<bool?> ExistsAsync(string logical, CancellationToken cancellationToken);

    Task<RedisValue> ControlIdAsync(string logical);

    /// <summary>Returns the source set script code.</summary>
    Task<long> SetAsync(string logical, string id, RedisValue bytes, long px, bool onlyIfAbsent);

    /// <summary>Returns the source delete script code.</summary>
    Task<long> DeleteAsync(string logical, string id);

    /// <summary>Returns the compare-delete script code.</summary>
    Task<long> CompareDeleteAsync(string logical, string id, RedisValue expected);

    /// <summary>Uses <c>SET NX</c> without consulting the control record.</summary>
    Task<bool> CreateAsync(string logical, RedisValue bytes, long px);
}
