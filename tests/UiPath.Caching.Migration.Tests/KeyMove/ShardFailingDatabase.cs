using System.Net;
using System.Reflection;
using StackExchange.Redis;

namespace UiPath.Caching.Migration.Tests.KeyMove;

/// <summary>A database that fails every command for a key whose slot one chosen master serves, as a shard that is down would, and runs the rest.</summary>
public class ShardFailingDatabase : DispatchProxy
{
    private IDatabase _inner = default!;

    public EndPoint? Failing { get; set; }

    /// <summary>When set, only the raw commands of this name fail on the chosen shard, such as PTTL, and every other command runs.</summary>
    public string? OnlyCommand { get; set; }

    public static IDatabase Over(IDatabase inner, out ShardFailingDatabase failing)
    {
        var proxy = Create<IDatabase, ShardFailingDatabase>();
        failing = (ShardFailingDatabase)(object)proxy;
        failing._inner = inner;
        return proxy;
    }

    /// <summary>The master that serves the slot of <paramref name="key"/> in the client's view of the cluster.</summary>
    public static EndPoint? OwnerOf(IConnectionMultiplexer mux, RedisKey key)
    {
        var slot = mux.HashSlot(key);
        return mux.GetServers().First(s => s.IsConnected).ClusterConfiguration?.GetBySlot(slot)?.EndPoint;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (Failing is { } failing && Selected(targetMethod!, args) && FirstKey(args) is { } key && Equals(OwnerOf(_inner.Multiplexer, key), failing))
        {
            throw new RedisException("The shard of this key is down.");
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

    private static RedisKey? FirstKey(object?[]? args)
    {
        foreach (var arg in args ?? [])
        {
            switch (arg)
            {
                case RedisKey key when key != default(RedisKey):
                    return key;
                case RedisKey[] { Length: > 0 } keys:
                    return keys[0];
                case System.Collections.IEnumerable raw and not string when raw.OfType<RedisKey>().FirstOrDefault() is var first && first != default(RedisKey):
                    return first;
            }
        }

        return null;
    }

    private bool Selected(MethodInfo method, object?[]? args) =>
        OnlyCommand is not { } only || (method.Name.StartsWith("Execute", StringComparison.Ordinal) && string.Equals(args?[0] as string, only, StringComparison.OrdinalIgnoreCase));
}
