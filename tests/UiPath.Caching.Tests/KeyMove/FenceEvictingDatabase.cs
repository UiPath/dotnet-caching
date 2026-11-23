using System.Reflection;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// A database whose first batches delete the key they were about to give a life, so the fence they read back is gone: an eviction between SET NX and GET.
internal sealed class FenceEvictingDatabase
{
    private readonly IDatabase _real;
    private int _toEvict;
    private int _batches;

    public FenceEvictingDatabase(IDatabase real, int toEvict)
    {
        _real = real;
        _toEvict = toEvict;
        Database = InterceptingProxy.Wrap(real, InterceptDatabase);
    }

    public IDatabase Database { get; }

    public int Batches => Volatile.Read(ref _batches);

    private object? InterceptDatabase(MethodInfo method, object?[] args)
    {
        if (method.Name != nameof(IDatabase.CreateBatch))
        {
            return InterceptingProxy.Proceed;
        }

        Interlocked.Increment(ref _batches);
        var inner = _real.CreateBatch(args.Length > 0 ? args[0] : null);
        if (Interlocked.Decrement(ref _toEvict) < 0)
        {
            return inner;
        }

        return InterceptingProxy.Wrap(inner, (m, a) =>
            m.Name == nameof(IBatch.KeyExpireAsync) ? inner.KeyDeleteAsync((RedisKey)a[0]!, (CommandFlags)a[^1]!) : InterceptingProxy.Proceed);
    }
}
