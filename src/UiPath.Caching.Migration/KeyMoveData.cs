using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>What the data path of a cache needs from a started key move node: the data view, the scripts over it, the key names, and the connection's events.</summary>
/// <param name="Db">The view that adds the connector prefix; keys are logical.</param>
/// <param name="ConnectionEvents">Counts every connection restore and configuration change; a promotion abandons when it moved since its capture.</param>
internal sealed record KeyMoveData(IDatabase Db, MoveScripts Scripts, MoveKeyNames Names, Func<long> ConnectionEvents, Func<bool> IsConnected);
