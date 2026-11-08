using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Benchmarks;

/// <summary>A Redis connector with no server: GET and SET hit a dictionary, and each GET completes on one reader thread that hands its continuation to the thread pool, as StackExchange.Redis does.</summary>
internal sealed class FakeRedisConnector : IRedisConnector
{
    private readonly FakeRedisStore _store = new();

    public FakeRedisConnector()
    {
        Database = FakeProxy<IDatabase>.Create(_store);
        Subscriber = FakeProxy<ISubscriber>.Create(_store);
    }

    public event EventHandler? OnConnectionFailed { add { } remove { } }

    public event EventHandler? OnConnectionRestored { add { } remove { } }

    public event EventHandler? OnReconnected { add { } remove { } }

    public bool IsConnected => true;

    public Version Version { get; } = new(7, 4);

    public IDatabase Database { get; }

    public ISubscriber Subscriber { get; }

    /// <summary>GETs sent so far.</summary>
    public long Gets => Volatile.Read(ref _store.GetCount);

    /// <summary>Removes the key most recently SET, in constant time.</summary>
    public void ForgetLastWrite()
    {
        var key = _store.LastSet;
        if (key is { } k)
        {
            _store.Values.TryRemove(k, out _);
        }
    }

    public void ForceReconnect()
    {
    }

    public EndPoint[] GetEndPoints(bool configuredOnly = false) => [new DnsEndPoint("fake", 6379)];

    public void Dispose() => _store.Dispose();
}

internal sealed class FakeRedisStore : IDisposable
{
    public readonly ConcurrentDictionary<RedisKey, RedisValue> Values = new();
    public long GetCount;
    public RedisKey? LastSet;
    private readonly BlockingCollection<(TaskCompletionSource<RedisValue> Reply, RedisValue Value)> _replies = [];
    private readonly Thread _reader;

    public FakeRedisStore()
    {
        _reader = new Thread(Read) { IsBackground = true, Name = "fake-redis-reader" };
        _reader.Start();
    }

    public Task<RedisValue> Get(RedisKey key)
    {
        Interlocked.Increment(ref GetCount);
        var reply = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        _replies.Add((reply, Values.TryGetValue(key, out var v) ? v : RedisValue.Null));
        return reply.Task;
    }

    public void Dispose() => _replies.CompleteAdding();

    private void Read()
    {
        foreach (var (reply, value) in _replies.GetConsumingEnumerable())
        {
            reply.TrySetResult(value);
        }
    }
}

/// <summary>Answers every member with its default, except the string GET and SET the store serves.</summary>
internal class FakeProxy<T> : DispatchProxy
    where T : class
{
    private static readonly ConcurrentDictionary<Type, object?> Defaults = new();
    private FakeRedisStore _store = default!;

    public static T Create(FakeRedisStore store)
    {
        var proxy = DispatchProxy.Create<T, FakeProxy<T>>();
        ((FakeProxy<T>)(object)proxy)._store = store;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        if (method.Name == "StringGetAsync" && args is [RedisKey key, ..])
        {
            return _store.Get(key);
        }

        if (method.Name == "StringSetAsync" && args is [RedisKey setKey, RedisValue value, ..])
        {
            _store.Values[setKey] = value;
            _store.LastSet = setKey;
            return Task.FromResult(true);
        }

        return Defaults.GetOrAdd(method.ReturnType, DefaultOf);
    }

    private static object? DefaultOf(Type type)
    {
        if (type == typeof(void))
        {
            return null;
        }

        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var arg = type.GetGenericArguments()[0];
            var value = arg.IsValueType ? Activator.CreateInstance(arg) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(arg).Invoke(null, [value]);
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
