using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

// The condition plan and read-back predicate are used on servers that refuse scripts.
internal sealed record RecordOp(
    RedisKey Key,
    Func<ITransaction, Task<RedisResult>> Script,
    Func<HashEntry[], ConditionPlan> Plan,
    Func<HashEntry[], bool> Applied,
    LeaseFence? Fence = null);
