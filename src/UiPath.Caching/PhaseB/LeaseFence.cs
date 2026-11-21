using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The lease key and the token a mutation must still find in it; the key shares the state hash's slot, so the script checks both atomically.</summary>
internal sealed record LeaseFence(RedisKey Key, string Token);
