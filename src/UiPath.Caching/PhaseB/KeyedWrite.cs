using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>A transaction for <see cref="ServerBarrier"/> and the key that says which master it goes to.</summary>
/// <param name="Key">A key the transaction writes; the barrier resolves its master.</param>
/// <param name="Queue">Queues the writes on the transaction; it runs again on every attempt, so the writes must be safe to repeat.</param>
internal readonly record struct KeyedWrite(RedisKey Key, Action<ITransaction> Queue);
