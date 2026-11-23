using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>What the router's data path takes from a started node: the data view with the connector prefix, the scripts over it, the names, and the connection.</summary>
internal sealed record KeyMoveDataAccess(IDatabase Db, MoveScripts Scripts, MoveKeyNames Names, IConnectionMultiplexer Mux);
