using System.Reflection;

namespace UiPath.Caching.Tests.KeyMove;

// Forwards every call of an interface to a target, unless the interceptor answers it first.
public class InterceptingProxy : DispatchProxy
{
    public static readonly object Proceed = new();

    private object _target = null!;
    private Func<MethodInfo, object?[], object?> _intercept = null!;

    public static T Wrap<T>(T target, Func<MethodInfo, object?[], object?> intercept)
        where T : class
    {
        var proxy = Create<T, InterceptingProxy>();
        var self = (InterceptingProxy)(object)proxy;
        self._target = target;
        self._intercept = intercept;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var arguments = args ?? [];
        var answer = _intercept(targetMethod, arguments);
        if (!ReferenceEquals(answer, Proceed))
        {
            return answer;
        }

        try
        {
            return targetMethod.Invoke(_target, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
