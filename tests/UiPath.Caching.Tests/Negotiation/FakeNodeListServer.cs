using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.Negotiation;

/// <summary>A server whose clock a test sets, holding one hash, answering the node list's scripts or, with scripts denied, its plain commands.</summary>
internal sealed class FakeNodeListServer
{
    private readonly Dictionary<string, string> _hash = [];
    private int _inFlight;

    public FakeNodeListServer(bool scripts = true)
    {
        Database = Substitute.For<IDatabase>();
        Connector = Substitute.For<IRedisConnector>();
        Connector.Database.Returns(Database);
        Database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(call => Failure is { } failure ? Task.FromException<RedisResult>(failure) : scripts
                ? (WriteDelay is { } delay && call.ArgAt<string>(0).Contains("HSET", StringComparison.Ordinal)
                    ? DelayedScript(delay, call.ArgAt<string>(0), call.ArgAt<RedisValue[]>(2))
                    : Task.FromResult(Script(call.ArgAt<string>(0), call.ArgAt<RedisValue[]>(2))))

#pragma warning disable CS0618 // The only public constructor of a server error.
                : Task.FromException<RedisResult>(new RedisServerException("ERR unknown command 'EVAL'")));
#pragma warning restore CS0618
        Database.ExecuteAsync("TIME", Arg.Any<ICollection<object>?>(), Arg.Any<CommandFlags>())
            .Returns(_ => Task.FromResult(RedisResult.Create([(RedisValue)(NowMs / 1000), (RedisValue)((NowMs % 1000) * 1000)])));
        Database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                _hash[(string)call.ArgAt<RedisValue>(1)!] = (string)call.ArgAt<RedisValue>(2)!;
                return Task.FromResult(true);
            });
        Database.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(call => Task.FromResult(_hash.TryGetValue((string)call.ArgAt<RedisValue>(1)!, out var held) ? (RedisValue)held : RedisValue.Null));
        Database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(_ => Task.FromResult(_hash.Select(p => new HashEntry(p.Key, p.Value)).ToArray()));
        Database.HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(call => Task.FromResult(_hash.Remove((string)call.ArgAt<RedisValue>(1)!)));
        Database.HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(call => Task.FromResult((long)call.ArgAt<RedisValue[]>(1).Count(f => _hash.Remove((string)f!))));
    }

    public IDatabase Database { get; }

    /// <summary>When set, a write waits for it before it lands, as a slow Redis would hold it.</summary>
    public Func<Task>? WriteDelay { get; set; }

    /// <summary>The most writes that were in flight at once.</summary>
    public int MaxConcurrentWrites { get; private set; }

    /// <summary>When set, every script fails with it, as an unreachable Redis does.</summary>
    public Exception? Failure { get; set; }

    public IRedisConnector Connector { get; }

    /// <summary>The server's clock, in milliseconds since the epoch.</summary>
    public long NowMs { get; set; } = 1_000_000_000_000;

    public IReadOnlyDictionary<string, string> Hash => _hash;

    /// <summary>The node list commands that ran, in order: <c>write</c> and <c>read</c>.</summary>
    public List<string> Events { get; } = [];

    /// <summary>Stores a raw field, as another node or version would have written it.</summary>
    public void Put(string field, string value) => _hash[field] = value;

    private async Task<RedisResult> DelayedScript(Func<Task> delay, string script, RedisValue[] arguments)
    {
        var inFlight = Interlocked.Increment(ref _inFlight);
        MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, inFlight);
        try
        {
            await delay().ConfigureAwait(false);
            lock (_hash)
            {
                return Script(script, arguments);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private RedisResult Script(string script, RedisValue[] arguments)
    {
        if (script.Contains("HDEL", StringComparison.Ordinal))
        {
            var removed = 0;
            for (var i = 0; i < arguments.Length; i += 2)
            {
                if (_hash.TryGetValue((string)arguments[i]!, out var held) && held.StartsWith((string)arguments[i + 1]! + "|", StringComparison.Ordinal))
                {
                    _hash.Remove((string)arguments[i]!);
                    removed++;
                }
            }

            return RedisResult.Create((RedisValue)removed);
        }

        if (script.Contains("HSET", StringComparison.Ordinal))
        {
            Events.Add("write");
            _hash[(string)arguments[0]!] = NowMs.ToString(CultureInfo.InvariantCulture) + "|" + (string)arguments[1]!;
            return RedisResult.Create((RedisValue)NowMs);
        }

        Events.Add("read");
        var flat = _hash.SelectMany(p => new[] { RedisResult.Create((RedisValue)p.Key, ResultType.BulkString), RedisResult.Create((RedisValue)p.Value, ResultType.BulkString) }).ToArray();
        return RedisResult.Create([RedisResult.Create((RedisValue)NowMs), RedisResult.Create(flat)]);
    }
}
