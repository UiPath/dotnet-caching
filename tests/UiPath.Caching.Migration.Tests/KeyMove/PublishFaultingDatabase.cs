using System.Reflection;
using StackExchange.Redis;

namespace UiPath.Caching.Migration.Tests.KeyMove;

/// <summary>A database whose publish script (the PUB of the promotion) throws a given exception a set number of times, and passes every other command through.</summary>
public class PublishFaultingDatabase : DispatchProxy
{
    private const string PublishBody = "redis.call('SET', KEYS[2], ARGV[2])";

    private IDatabase _inner = default!;
    private Exception? _fault;
    private int _remaining;

    public int Faulted { get; private set; }

    /// <summary>Called when a publish is about to run.</summary>
    public Action? BeforePublish { get; set; }

    /// <summary>Called with the script's reply once a publish has run; a reply other than 1 changed nothing.</summary>
    public Action<RedisResult>? AfterPublish { get; set; }

    public static IDatabase Over(IDatabase inner, out PublishFaultingDatabase fake)
    {
        var proxy = Create<IDatabase, PublishFaultingDatabase>();
        fake = (PublishFaultingDatabase)(object)proxy;
        fake._inner = inner;
        return proxy;
    }

    /// <summary>The next <paramref name="times"/> publishes throw <paramref name="fault"/>.</summary>
    public void Fail(Exception fault, int times)
    {
        _fault = fault;
        Interlocked.Exchange(ref _remaining, times);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var isPublish = targetMethod!.Name == "ScriptEvaluateAsync" && args is [string script, ..] && script.Contains(PublishBody, StringComparison.Ordinal) && !script.Contains("staging", StringComparison.Ordinal);
        if (isPublish && Interlocked.Decrement(ref _remaining) >= 0)
        {
            Faulted++;
            return Task.FromException<RedisResult>(_fault!);
        }

        if (isPublish)
        {
            BeforePublish?.Invoke();
            if (AfterPublish is { } after)
            {
                var run = (Task<RedisResult>)Invoke(targetMethod, args, _inner);
                return run.ContinueWith(
                    t =>
                    {
                        var result = t.GetAwaiter().GetResult();
                        after(result);
                        return result;
                    },
                    TaskScheduler.Default);
            }
        }

        return Invoke(targetMethod, args, _inner);
    }

    private static object Invoke(MethodInfo method, object?[]? args, object target)
    {
        try
        {
            return method.Invoke(target, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
