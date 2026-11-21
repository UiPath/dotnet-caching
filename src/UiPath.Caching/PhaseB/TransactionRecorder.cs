using System.Reflection;
using System.Runtime.ExceptionServices;
using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>A transaction that forwards every call to the real one, with <see cref="CommandFlags.FireAndForget"/> cleared, and keeps each task it returns, so the barrier observes every queued command itself.</summary>
internal class TransactionRecorder : DispatchProxy
{
    private readonly List<Task> _queued = [];
    private ITransaction _inner = null!;

    /// <summary>Wraps <paramref name="inner"/>; the recorder holds the task of every command the caller queues on the returned transaction.</summary>
    public static ITransaction Wrap(ITransaction inner, out TransactionRecorder recorder)
    {
        var proxy = Create<ITransaction, TransactionRecorder>();
        recorder = (TransactionRecorder)(object)proxy;
        recorder._inner = inner;
        return proxy;
    }

    /// <summary>The tasks of the commands queued so far, in order.</summary>
    public IReadOnlyList<Task> Queued()
    {
        lock (_queued)
        {
            return [.. _queued];
        }
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        try
        {
            ClearFireAndForget(args);
            var result = targetMethod.Invoke(_inner, args);
            if (result is Task task)
            {
                lock (_queued)
                {
                    _queued.Add(task);
                }
            }

            return result;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    // A fire-and-forget command gets a completed placeholder task, so a failure at run time would go unseen.
    private static void ClearFireAndForget(object?[]? args)
    {
        for (var i = 0; args is not null && i < args.Length; i++)
        {
            if (args[i] is CommandFlags flags)
            {
                args[i] = flags & ~CommandFlags.FireAndForget;
            }
        }
    }
}
