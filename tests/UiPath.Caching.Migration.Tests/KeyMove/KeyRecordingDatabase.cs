using System.Reflection;
using System.Runtime.ExceptionServices;
using StackExchange.Redis;

namespace UiPath.Caching.Migration.Tests.KeyMove;

public class KeyRecordingDatabase : DispatchProxy
{
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly List<string> _touched = [];
    private IDatabase? _inner;

    public IReadOnlyList<string> Touched
    {
        get
        {
            lock (_gate)
            {
                return [.. _touched];
            }
        }
    }

    public static IDatabase Over(IDatabase inner, out KeyRecordingDatabase recorder)
    {
        var proxy = Create<IDatabase, KeyRecordingDatabase>();
        recorder = (KeyRecordingDatabase)(object)proxy;
        recorder._inner = inner;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        foreach (var arg in args ?? [])
        {
            Collect(arg);
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }

    private void Collect(object? arg)
    {
        switch (arg)
        {
            case RedisKey key:
                Add(key.ToString());
                break;
            case string text:
                Add(text);
                break;
            case IEnumerable<object?> items:
                foreach (var item in items)
                {
                    Collect(item);
                }

                break;
            case RedisKey[] keys:
                foreach (var key in keys)
                {
                    Add(key.ToString());
                }

                break;
        }
    }

    private void Add(string text)
    {
        lock (_gate)
        {
            _touched.Add(text);
        }
    }
}
