namespace UiPath.Caching.Redis;

/// <summary>A Redis keyspace a package occupies, so nothing else can be configured onto it.</summary>
public interface IReservedRedisKeyspace
{
    string Keyspace { get; }

    /// <summary>
    /// Who occupies it. Also the identity a repeat reservation is matched on, so make it specific to the
    /// package: "ICache", "ISetCache (UiPath.Caching.Queue)". Two packages sharing one owner string on one
    /// keyspace read as the same package reserving twice.
    /// </summary>
    string Owner { get; }
}
