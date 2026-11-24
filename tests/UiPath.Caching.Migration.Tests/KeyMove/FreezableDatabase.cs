using System.Reflection;
using StackExchange.Redis;

namespace UiPath.Caching.Migration.Tests.KeyMove;

/// <summary>A database that fails every command once frozen, as the connection of a process that was killed would.</summary>
public class FreezableDatabase : DispatchProxy
{
    private IDatabase _inner = default!;

    public bool Frozen { get; set; }

    public static IDatabase Over(IDatabase inner, out FreezableDatabase freezable)
    {
        var proxy = Create<IDatabase, FreezableDatabase>();
        freezable = (FreezableDatabase)(object)proxy;
        freezable._inner = inner;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (Frozen && targetMethod!.Name != "get_Database")
        {
            throw new RedisException("The connection of this node is gone.");
        }

        try
        {
            return targetMethod!.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
