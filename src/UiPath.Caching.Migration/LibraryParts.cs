using Microsoft.Extensions.Hosting;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration;

/// <param name="Started">The hosted services of the library stack that the router started and stops.</param>
/// <param name="ReleaseTopic">Removes this node's topic and its consumer group, giving up when the token is cancelled; none when absent.</param>
/// <param name="KeyMove">The key move node's plane, when the router has key move groups.</param>
/// <param name="Cache">The library cache behind <paramref name="Store"/>, for a conditional create and a refresh.</param>
/// <param name="CacheNullValues">The library stores a null as a value; a router write of null then stores it, and without it removes.</param>
/// <param name="Binder">Binds the byte codecs and key maps of the key move caches; none when the router has no key move group.</param>
/// <param name="HashStore">The library hash cache behind the hash caches of the router; none when the router has no hash cache.</param>
/// <param name="HashCache">The hash cache behind <paramref name="HashStore"/>, for a presence check and an expiry.</param>
/// <param name="HashTier">The shared tier of that hash cache, which a miss fill writes through.</param>
internal sealed record LibraryParts(
    IMigrationCache Store,
    IConnectionState Connection,
    ITopicProvider Topics,
    ICacheEventFactory Events,
    Uri Source,
    Func<TimeSpan> LibrarySettleWithin,
    IReadOnlyList<IHostedService>? Started = null,
    Func<TopicKey, CancellationToken, Task>? ReleaseTopic = null,
    IKeyMovePlane? KeyMove = null,
    ICache? Cache = null,
    bool CacheNullValues = false,
    KeyMoveBinder? Binder = null,
    MoveTargets? Movers = null,
    IMigrationHashCache? HashStore = null,
    IHashCache? HashCache = null,
    ISharedHashTier? HashTier = null)
{
    public IReadOnlyList<IHostedService> Started { get; } = Started ?? [];

    public Func<TopicKey, CancellationToken, Task> ReleaseTopicAsync { get; } = ReleaseTopic ?? ((_, _) => Task.CompletedTask);
}
