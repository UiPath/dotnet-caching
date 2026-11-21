using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The fence a Dst-side hash write replaces.</summary>
internal readonly record struct FenceBump(RedisKey Fence, string Nonce, long TtlMilliseconds);
