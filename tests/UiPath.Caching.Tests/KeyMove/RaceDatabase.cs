using System.Reflection;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// A database that lets another actor act at a chosen point of a read: before the first call of a method, or after its first call completed and before the caller sees the result.
internal static class RaceDatabase
{
    public static IDatabase Before(IDatabase real, Action action, params string[] methods)
    {
        var fired = 0;
        return InterceptingProxy.Wrap(real, (m, _) =>
        {
            if (methods.Contains(m.Name) && Interlocked.Exchange(ref fired, 1) == 0)
            {
                action();
            }

            return InterceptingProxy.Proceed;
        });
    }

    public static IDatabase AfterHashGetAll(IDatabase real, Func<Task> action)
    {
        var fired = 0;
        return InterceptingProxy.Wrap(real, (m, args) =>
            m.Name == nameof(IDatabase.HashGetAllAsync) && Interlocked.Exchange(ref fired, 1) == 0 ? ReadThenAct(real, m, args, action) : InterceptingProxy.Proceed);
    }

    // The first read of several fields completes, then the action runs, then the caller sees the result.
    public static IDatabase AfterHashGetFields(IDatabase real, Action action)
    {
        var fired = 0;
        return InterceptingProxy.Wrap(real, (m, args) =>
            m.Name == nameof(IDatabase.HashGetAsync) && m.ReturnType == typeof(Task<RedisValue[]>) && Interlocked.Exchange(ref fired, 1) == 0 ? ReadFieldsThenAct(real, m, args, action) : InterceptingProxy.Proceed);
    }

    private static async Task<RedisValue[]> ReadFieldsThenAct(IDatabase real, MethodInfo method, object?[] args, Action action)
    {
        var read = await (Task<RedisValue[]>)method.Invoke(real, args)!;
        action();
        return read;
    }

    private static async Task<HashEntry[]> ReadThenAct(IDatabase real, MethodInfo method, object?[] args, Func<Task> action)
    {
        var read = await (Task<HashEntry[]>)method.Invoke(real, args)!;
        await action();
        return read;
    }
}
