using System.Reflection;
using System.Runtime.ExceptionServices;
using StackExchange.Redis;
using StackExchange.Redis.Maintenance;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>A proxy that forwards every call to a real <typeparamref name="T"/>, after a test has looked at it, answered it or changed the reply.</summary>
internal class Forwarding<T> : DispatchProxy
    where T : class
{
    private T _inner = default!;

    /// <summary>Returns true, with the answer, to handle a call without forwarding it; may throw.</summary>
    public Func<MethodInfo, object?[], (bool Handled, object? Result)>? Before { get; set; }

    public Func<MethodInfo, object?[], object?, object?>? After { get; set; }

    public static T Wrap(T inner, Func<MethodInfo, object?[], (bool Handled, object? Result)>? before = null, Func<MethodInfo, object?[], object?, object?>? after = null)
    {
        var proxy = Create<T, Forwarding<T>>();
        var forwarding = (Forwarding<T>)(object)proxy;
        forwarding._inner = inner;
        forwarding.Before = before;
        forwarding.After = after;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        args ??= [];
        if (Before?.Invoke(method, args) is { Handled: true } handled)
        {
            return handled.Result;
        }

        object? result;
        try
        {
            result = method.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
            throw;
        }

        return After is { } after ? after(method, args, result) : result;
    }
}

/// <summary>A connector over a real one whose database can be watched or answered for.</summary>
internal sealed class InterceptingConnector(IRedisConnector inner, Func<MethodInfo, object?[], (bool Handled, object? Result)>? before = null, Func<MethodInfo, object?[], object?, object?>? after = null) : IRedisConnector
{
    private readonly IDatabase _database = Forwarding<IDatabase>.Wrap(inner.Database, before, after);

    public event EventHandler? OnConnectionFailed
    {
        add => inner.OnConnectionFailed += value;
        remove => inner.OnConnectionFailed -= value;
    }

    public event EventHandler? OnConnectionRestored
    {
        add => inner.OnConnectionRestored += value;
        remove => inner.OnConnectionRestored -= value;
    }

    public event EventHandler? OnReconnected
    {
        add => inner.OnReconnected += value;
        remove => inner.OnReconnected -= value;
    }

    public event EventHandler<ServerMaintenanceEvent>? ServerMaintenance
    {
        add => inner.ServerMaintenance += value;
        remove => inner.ServerMaintenance -= value;
    }

    public bool IsConnected => inner.IsConnected;

    public Version Version => inner.Version;

    public IDatabase Database => _database;

    public ISubscriber Subscriber => inner.Subscriber;

    public void ForceReconnect() => inner.ForceReconnect();

    public System.Net.EndPoint[] GetEndPoints(bool configuredOnly = false) => inner.GetEndPoints(configuredOnly);

    public IEnumerable<IServer> GetPrimaries() => inner.GetPrimaries();

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);

    public void Dispose() => inner.Dispose();
}
