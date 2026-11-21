using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The data key of a fenced script with its fence and the control record of its slot.</summary>
internal readonly record struct FencedKeys(RedisKey Fence, RedisKey Data, RedisKey Control);
