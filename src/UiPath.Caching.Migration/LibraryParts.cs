using Microsoft.Extensions.Hosting;
using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Migration;

/// <param name="Started">The hosted services of the library stack that the router started and stops.</param>
/// <param name="ReleaseTopic">Removes this node's topic and its consumer group; none when absent.</param>
internal sealed record LibraryParts(
    IMigrationCache Store,
    IConnectionState Connection,
    ITopicProvider Topics,
    ICacheEventFactory Events,
    Uri Source,
    Func<TimeSpan> LibrarySettleWithin,
    IReadOnlyList<IHostedService>? Started = null,
    Func<TopicKey, Task>? ReleaseTopic = null)
{
    public IReadOnlyList<IHostedService> Started { get; } = Started ?? [];

    public Func<TopicKey, Task> ReleaseTopicAsync { get; } = ReleaseTopic ?? (_ => Task.CompletedTask);
}
