using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Migration;

internal sealed record RouterContext(
    RouterCore Core,
    ILibraryStore Store,
    IConnectionState Connection,
    ITopicProvider Topics,
    ICacheEventFactory Events,
    Uri Source,
    Func<TimeSpan> LibrarySettleWithin);
