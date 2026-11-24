using System.Reflection;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// A database that runs an action right after the n-th call of one of its async methods has completed and before the caller sees the result: what the caller read is then stale by the action.
internal static class InterleavedDatabase
{
    public static IDatabase After(IDatabase database, string method, Func<Task> action, int nth = 1)
    {
        var seen = 0;
        return InterceptingProxy.Wrap(database, (m, args) =>
        {
            if (m.Name != method || Interlocked.Increment(ref seen) != nth)
            {
                return InterceptingProxy.Proceed;
            }

            var running = m.Invoke(database, args)!;
            return typeof(InterleavedDatabase).GetMethod(nameof(RunAsync), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(m.ReturnType.GetGenericArguments()[0]).Invoke(null, [running, action]);
        });
    }

    private static async Task<T> RunAsync<T>(Task<T> read, Func<Task> action)
    {
        var result = await read.ConfigureAwait(false);
        await action().ConfigureAwait(false);
        return result;
    }
}
