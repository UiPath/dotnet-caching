using StackExchange.Redis;

namespace UiPath.Caching.Migration;

/// <summary>
/// The switch state as one string key per application and group, <c>{prefix}mg:sw:{app}:{group}</c>, with no braces and no expiry. The prefix is the library's
/// key prefix when the database adds none of its own; a database that does add it already carries it.
/// </summary>
internal sealed class RedisPinStore(RouterAdvertisementStore nodes, string keyPrefix, string? scope) : IPinStore
{
    private const string Switched = "1";

    public RedisKey StateKey(string group) => $"{keyPrefix}mg:sw:{Encode(scope ?? "_")}:{Encode(group)}";

    public async Task<SwitchRead> ReadStateAsync(string group, CancellationToken cancellationToken)
    {
        var db = await nodes.DatabaseAsync(cancellationToken).ConfigureAwait(false);
        var value = await db.StringGetAsync(StateKey(group), CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (value.IsNull)
        {
            return SwitchRead.Unswitched;
        }

        return (string?)value == Switched ? SwitchRead.Switched : SwitchRead.Unreadable;
    }

    public async Task MarkSwitchedAsync(string group, CancellationToken cancellationToken)
    {
        var db = await nodes.DatabaseAsync(cancellationToken).ConfigureAwait(false);
        await db.StringSetAsync(StateKey(group), Switched, flags: CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<NodeEntry>> ReadNodesAsync(CancellationToken cancellationToken) => nodes.ReadAllAsync(cancellationToken);

    private static string Encode(string text) =>
        text.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("{", "%7B", StringComparison.Ordinal)
            .Replace("}", "%7D", StringComparison.Ordinal);
}
