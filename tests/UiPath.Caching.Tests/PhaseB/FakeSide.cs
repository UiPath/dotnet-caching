using System.Net;
using System.Reflection;
using StackExchange.Redis;
using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

/// <summary>A cluster-like side over fake primaries: slots split evenly between them, every command answered and the keys it touched recorded.</summary>
internal sealed class FakeSide
{
    private readonly FakePrimary[] _primaries;
    private readonly Dictionary<string, int> _slots = Enumerable.Range(0, HashSlot.Count).ToDictionary(static slot => SlotTags.For(slot));
    private readonly IServer[] _servers;
    private readonly IConnectionMultiplexer _multiplexer;

    public FakeSide(params FakePrimary[] primaries)
    {
        _primaries = primaries;
        _servers = [.. primaries.Select(Server)];
        _multiplexer = InterceptingProxy.Wrap<IConnectionMultiplexer>(null!, (method, args) => method.Name switch
        {
            nameof(IConnectionMultiplexer.GetServers) => _servers,
            nameof(IConnectionMultiplexer.GetServer) when args[0] is EndPoint endpoint => _servers.Single(s => Equals(s.EndPoint, endpoint)),
            _ => throw new NotSupportedException(method.Name),
        });
        Database = InterceptingProxy.Wrap<IDatabase>(null!, Intercept);
    }

    public IDatabase Database { get; }

    /// <summary>Thrown by the first hash write, to stand for a refusal that is not a denial.</summary>
    public Exception? DatabaseFault { get; init; }

    /// <summary>Every key a command touched, in order.</summary>
    public List<string> WrittenKeys { get; } = [];

    public List<EndPoint> Scanned { get; } = [];

    public EndPoint Owner(string key)
    {
        var tag = key[(key.IndexOf('{', StringComparison.Ordinal) + 1)..key.IndexOf('}', StringComparison.Ordinal)];
        return _primaries[_slots[tag] * _primaries.Length / HashSlot.Count].EndPoint;
    }

    private static object? Default(MethodInfo method)
    {
        var type = method.ReturnType;
        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var result = type.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [result.IsValueType ? Activator.CreateInstance(result) : null]);
        }

        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }

    private static Task<RedisResult> Number(long value) => Task.FromResult(RedisResult.Create((RedisValue)value, ResultType.Integer));

    private object? Intercept(MethodInfo method, object?[] args)
    {
        if (method.Name == nameof(IDatabase.IdentifyEndpointAsync))
        {
            return Task.FromResult<EndPoint?>(Owner(((RedisKey)args[0]!).ToString()));
        }

        Record(args);
        return method.Name switch
        {
            "get_Multiplexer" => _multiplexer,
            nameof(IDatabase.CreateTransaction) => InterceptingProxy.Wrap<ITransaction>(null!, InterceptTransaction),
            nameof(IDatabase.HashSetAsync) when DatabaseFault is not null => Task.FromException<bool>(DatabaseFault),
            _ => Default(method),
        };
    }

    private object? InterceptTransaction(MethodInfo method, object?[] args)
    {
        Record(args);
        return method.Name == nameof(ITransaction.ExecuteAsync) && method.GetParameters()[0].ParameterType == typeof(CommandFlags) ? Task.FromResult(true) : Default(method);
    }

    private void Record(object?[] args)
    {
        foreach (var arg in args)
        {
            switch (arg)
            {
                case RedisKey key:
                    WrittenKeys.Add((string)key!);
                    break;
                case RedisKey[] keys:
                    WrittenKeys.AddRange(keys.Select(static k => (string)k!));
                    break;
            }
        }
    }

    private IServer Server(FakePrimary primary) => InterceptingProxy.Wrap<IServer>(null!, (method, args) => method.Name switch
    {
        "get_EndPoint" => primary.EndPoint,
        "get_IsReplica" => false,
        "get_IsConnected" => primary.Connected,
        "get_ServerType" => ServerType.Cluster,
        "get_Version" => new Version(7, 4),
        nameof(IServer.RoleAsync) => primary.Role is null ? Task.FromResult(FakeRoles.Master(primary.Replicas)) : Task.FromException<Role>(primary.Role),
        nameof(IServer.ExecuteAsync) => Execute(primary, (string)args[0]!),
        _ => throw new NotSupportedException(method.Name),
    });

    private Task<RedisResult> Execute(FakePrimary primary, string command)
    {
        switch (command)
        {
            case "WAIT":
                return primary.Wait is Exception wait ? Task.FromException<RedisResult>(wait) : Number(Convert.ToInt64(primary.Wait ?? 1L, System.Globalization.CultureInfo.InvariantCulture));
            case "CONFIG":
                return primary.Config is not null
                    ? Task.FromException<RedisResult>(primary.Config)
                    : Task.FromResult(RedisResult.Create([RedisResult.Create("maxmemory-policy", ResultType.BulkString), RedisResult.Create("noeviction", ResultType.BulkString)]));
            case "INFO":
                return primary.Info is not null ? Task.FromException<RedisResult>(primary.Info) : Task.FromResult(RedisResult.Create("maxmemory_policy:noeviction\r\n", ResultType.BulkString));
            case "SCAN":
                Scanned.Add(primary.EndPoint);
                return primary.Scan is not null
                    ? Task.FromException<RedisResult>(primary.Scan)
                    : Task.FromResult(RedisResult.Create([RedisResult.Create("0", ResultType.BulkString), RedisResult.Create(Array.Empty<RedisResult>())]));
            default:
                return Number(0);
        }
    }
}
