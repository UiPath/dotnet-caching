using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed class MovePlacement(MoveViews views, string app, string libraryPrefix, SideCaps src, SideCaps dst, bool dstCluster, bool srcCluster = false)
{
    private readonly MoveKeyNames _src = new(views.SourcePrefix, libraryPrefix, srcCluster || src.SlotEnforced);
    private readonly MoveKeyNames _dst = new(string.Empty, libraryPrefix, dstCluster || dst.SlotEnforced);

    public string App { get; } = app;

    public MoveKeyNames Names(RecordSide side) => side == RecordSide.Src ? _src : _dst;

    public (IDatabase Db, RedisKey Key) State(string group) => (views.Destination, _dst.State(group));

    public (IDatabase Db, RedisKey Key) Lead(string group) => (views.Destination, _dst.Lead(group));

    public (IDatabase Db, RedisKey Key) Claim(RecordSide side) => (Database(side), Names(side).Claim(App));

    public IReadOnlyList<(int Slot, RedisKey Key)> Control(RecordSide side, string group) => Names(side).ControlRecords(group);

    public RedisKey ForwardFence(string nativeKey) => _dst.ForwardFence(nativeKey);

    public RedisKey ReverseFence(string legacyKey) => _src.ReverseFence(legacyKey);

    public IDatabase Adverts(RecordSide side) => Database(side);

    private IDatabase Database(RecordSide side) => side == RecordSide.Src ? views.SourcePrefixed : views.Destination;
}
