using System.Reflection;

namespace UiPath.Caching.Migration.Tests;

// The library's cache as a script: each call answers what the test set for its method name, and a method the test did not script answers a completed default.
public class ScriptedCache : DispatchProxy
{
    public Dictionary<string, Func<object?[], object?>> Answers { get; } = [];

    public List<string> Calls { get; } = [];

    internal static (ICache Cache, ScriptedCache Script) Create()
    {
        var cache = Create<ICache, ScriptedCache>();
        return (cache, (ScriptedCache)(object)cache);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        Calls.Add(targetMethod.Name);
        if (Answers.TryGetValue(targetMethod.Name, out var answer))
        {
            return AsReturn(targetMethod.ReturnType, answer(args ?? []));
        }

        return targetMethod.ReturnType == typeof(void) ? null : Activator.CreateInstance(targetMethod.ReturnType);
    }

    private static bool IsAwaitable(Type type) =>
        type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(ValueTask<>) || type.GetGenericTypeDefinition() == typeof(Task<>));

    // An answer may give the plain result, or a Task of it, and the proxy wraps it in the method's own return type.
    private static object? AsReturn(Type returnType, object? value)
    {
        if ((value is not null && returnType.IsInstanceOfType(value)) || !IsAwaitable(returnType))
        {
            return value;
        }

        var result = returnType.GetGenericArguments()[0];
        var task = value is Task ? value : typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value]);
        return returnType.GetGenericTypeDefinition() == typeof(Task<>) ? task : Activator.CreateInstance(returnType, task);
    }
}
