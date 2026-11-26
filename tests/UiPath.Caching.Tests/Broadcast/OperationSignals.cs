using System.Reflection;
using System.Runtime.ExceptionServices;

namespace UiPath.Caching.Tests.Broadcast;

internal sealed class OperationSignals
{
    private readonly object _gate = new();
    private readonly List<(Func<bool> Condition, TaskCompletionSource<bool> Done)> _waiting = [];

    internal static OperationSignals Shared { get; } = new();

    public Task<bool> WhenAsync(Func<bool> condition)
    {
        lock (_gate)
        {
            if (condition())
            {
                return Task.FromResult(true);
            }
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Add((condition, done));
            return done.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    public void Completed()
    {
        lock (_gate)
        {
            for (var i = _waiting.Count - 1; i >= 0; i--)
            {
                if (_waiting[i].Condition())
                {
                    _waiting[i].Done.TrySetResult(true);
                    _waiting.RemoveAt(i);
                }
            }
        }
    }

    public T Observe<T>(T target) where T : class
    {
        var proxy = DispatchProxy.Create<T, ObservedCalls>();
        var calls = (ObservedCalls)(object)proxy;
        calls.Target = target;
        calls.Completed = Completed;
        return proxy;
    }

    public class ObservedCalls : DispatchProxy
    {
        internal object Target { get; set; } = null!;
        internal Action Completed { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                return targetMethod!.Invoke(Target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            finally
            {
                Completed();
            }
        }
    }
}
