using System.Reflection;
using StackExchange.Redis;

namespace UiPath.Caching.Migration.Tests.KeyMove;

/// <summary>A database whose CAP script answers <c>{-4, marker}</c> a set number of times, and passes every other command and every later CAP through.</summary>
public class CapFakingDatabase : DispatchProxy
{
    private const string CapHead = "if redis.call('HGET', KEYS[3], 'id') ~= ARGV[3] then return {-1} end";

    private IDatabase _inner = default!;
    private int _remaining;
    private string _marker = "marker";

    public int Faked { get; private set; }

    public static IDatabase Over(IDatabase inner, out CapFakingDatabase fake)
    {
        var proxy = Create<IDatabase, CapFakingDatabase>();
        fake = (CapFakingDatabase)(object)proxy;
        fake._inner = inner;
        return proxy;
    }

    /// <summary>The next <paramref name="times"/> captures answer -4 with <paramref name="marker"/>.</summary>
    public void Supersede(int times, string marker = "marker")
    {
        _marker = marker;
        Interlocked.Exchange(ref _remaining, times);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "ScriptEvaluateAsync" && args is [string script, ..] && script.Contains(CapHead, StringComparison.Ordinal) && Interlocked.Decrement(ref _remaining) >= 0)
        {
            Faked++;
            return Task.FromResult(RedisResult.Create([RedisResult.Create((RedisValue)(-4L)), RedisResult.Create((RedisValue)_marker)]));
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
