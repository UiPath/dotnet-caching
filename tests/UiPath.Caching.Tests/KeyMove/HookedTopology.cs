using System.Net;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

// The real topology with seams: a test can change the slot map between two readings and decide what each WAIT returns.
internal sealed class HookedTopology(IStateTopology inner) : IStateTopology
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<EndPoint, int> _openings = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<EndPoint, IConnectionMultiplexer> _connections = new();

    private int _reads;
    private int _opened;
    private int _waits;
    private int _controlCalls;
    private int _stateCalls;
    private int _execs;

    // Given the real layout and the number of the reading (from 1), the layout the controller sees.
    public Func<ClusterLayout, int, ClusterLayout>? Layout { get; set; }

    // The result of a WAIT (endpoint, number of the WAIT from 1); null runs the real command.
    public Func<EndPoint, int, long?>? Wait { get; set; }

    // The owner a connection reports for a key (the connection's endpoint, the key, the number of the call from 1); null asks the real connection.
    public Func<EndPoint, RedisKey, int, EndPoint?>? ControlOwner { get; set; }

    public Func<EndPoint, RedisKey, int, EndPoint?>? StateOwner { get; set; }

    // Runs once after the first control record read that goes through a dedicated connection, before its caller sees the result.
    public Func<Task>? AfterControlRead { get; set; }

    // What a master reports (replicas, eviction policy); null asks the real server.
    public Func<EndPoint, MasterProbe?>? Probe { get; set; }

    // Runs before every WAIT of a barrier, given the master and its real server (a test can kill the connection or promote a replica here); the WAIT then goes on as usual.
    public Func<EndPoint, IServer, Task>? BeforeWait { get; set; }

    // Rewrites the text of every INFO a barrier reads after its WAIT (master, text); the INFO inside the transaction is not seen here.
    public Func<EndPoint, string, string>? Info { get; set; }

    // Whether the reply of the EXEC of the barrier's transaction (number from 1) is lost after the server applied it.
    public Func<int, bool>? LoseReply { get; set; }

    // Runs before a dedicated connection to a master is opened, given the master and the number of the opening for it (from 1); a retry opens a second one.
    public Func<EndPoint, int, Task>? BeforeOpen { get; set; }

    // Whether the EXEC of the barrier's transaction (number from 1) fails with no reply before the server ran it: the write did not land, though the client cannot tell.
    public Func<int, bool>? LoseBeforeApply { get; set; }

    // The server refuses scripts: a script alone fails with NOPERM, and a transaction that queues one is discarded with EXECABORT. The scripts of the channels are then built with <see cref="ServerVersion"/>.
    public bool RefuseScripts { get; set; }

    public Version? ServerVersion { get; set; }

    public int Execs => Volatile.Read(ref _execs);

    public int Opened => Volatile.Read(ref _opened);

    public int Waits => Volatile.Read(ref _waits);

    public IConnectionMultiplexer ConnectionFor(EndPoint endpoint) => _connections[endpoint];

    public async Task<ClusterLayout> ReadLayoutAsync(CancellationToken cancellationToken)
    {
        var real = await inner.ReadLayoutAsync(cancellationToken);
        return Layout is { } hook ? hook(real, Interlocked.Increment(ref _reads)) : real;
    }

    public async Task<MasterProbe> ProbeAsync(EndPoint master, CancellationToken cancellationToken) =>
        Probe?.Invoke(master) ?? await inner.ProbeAsync(master, cancellationToken);

    public async Task<IMasterChannel> OpenAsync(EndPoint master, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _opened);
        if (BeforeOpen is { } before)
        {
            await before(master, _openings.AddOrUpdate(master, 1, (_, n) => n + 1));
        }

        var channel = await inner.OpenAsync(master, cancellationToken);
        _connections[master] = channel.StateDatabase.Multiplexer;
        return new HookedChannel(channel, this);
    }

    private sealed class HookedChannel(IMasterChannel channel, HookedTopology owner) : IMasterChannel
    {
        private MoveScripts? _control;
        private MoveScripts? _state;

        public EndPoint Endpoint => channel.Endpoint;

        public MoveScripts Control => owner.RefuseScripts ? _control ??= new MoveScripts(ControlDatabase, true, owner.ServerVersion) : channel.Control;

        public IDatabase ControlDatabase => Hooked(owner.AfterControlRead is { } after ? RaceControl(after) : channel.ControlDatabase);

        public MoveScripts State => owner.RefuseScripts ? _state ??= new MoveScripts(StateDatabase, true, owner.ServerVersion) : channel.State;

        public IDatabase StateDatabase => Hooked(channel.StateDatabase);

        public async Task<EndPoint?> ControlOwnerAsync(RedisKey key) =>
            owner.ControlOwner?.Invoke(Endpoint, key, Interlocked.Increment(ref owner._controlCalls)) ?? await channel.ControlOwnerAsync(key);

        public async Task<EndPoint?> StateOwnerAsync(RedisKey key) =>
            owner.StateOwner?.Invoke(Endpoint, key, Interlocked.Increment(ref owner._stateCalls)) ?? await channel.StateOwnerAsync(key);

        public ValueTask DisposeAsync() => channel.DisposeAsync();

        private static async Task<bool> Completed(Task source)
        {
            await source;
            return true;
        }

        private static Task<T> Mirror<T>(Task<T> source, List<Action<Exception>> pending)
        {
            var mirror = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pending)
            {
                pending.Add(error => mirror.TrySetException(error));
            }

            source.ContinueWith(
                done =>
                {
                    if (done.IsFaulted)
                    {
                        mirror.TrySetException(done.Exception!.InnerExceptions);
                    }
                    else if (done.IsCanceled)
                    {
                        mirror.TrySetCanceled();
                    }
                    else
                    {
                        mirror.TrySetResult(done.Result);
                    }
                },
                TaskScheduler.Default);
            return mirror.Task;
        }

        private static async Task<Role> WithReplicaAsync(IServer real)
        {
            var role = await real.RoleAsync();
            return role is Role.Master { Replicas.Count: 0 } ? FakeRoles.Master(1) : role;
        }

        private static async Task<RedisValue[]> ReadThenAct(IDatabase real, System.Reflection.MethodInfo method, object?[] args, Func<Task> after)
        {
            var read = await (Task<RedisValue[]>)method.Invoke(real, args)!;
            await after();
            return read;
        }

        // A database whose servers answer WAIT with what the hook says, so a barrier that runs its own WAIT is steered the same way; its transactions can lose the reply of EXEC.
        private IDatabase Hooked(IDatabase database) =>
            InterceptingProxy.Wrap(database, (method, args) => method.Name switch
            {
                "get_Multiplexer" => HookedMultiplexer(database.Multiplexer),
                nameof(IDatabase.CreateTransaction) => HookedTransaction(database.CreateTransaction(args[0])),
                nameof(IDatabase.ScriptEvaluateAsync) when owner.RefuseScripts && args[0] is string => Task.FromException<RedisResult>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM this user has no permissions to run the 'eval' command")),
                _ => InterceptingProxy.Proceed,
            });

        private ITransaction HookedTransaction(ITransaction real)
        {
            var refused = false;
            var pending = new List<Action<Exception>>();
            return InterceptingProxy.Wrap(real, (method, args) =>
            {
                // With a hook that loses an EXEC before it runs, the commands queued on that transaction fail with it, as the client fails them when the connection drops.
                if (owner.LoseBeforeApply is not null && method.ReturnType == typeof(Task) && args is not [CommandFlags])
                {
                    return Mirror(Completed((Task)method.Invoke(real, args)!), pending);
                }

                if (owner.LoseBeforeApply is not null && method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) && args is not [CommandFlags])
                {
                    return typeof(HookedChannel).GetMethod(nameof(Mirror), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                        .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0])
                        .Invoke(null, [method.Invoke(real, args), pending]);
                }

                if (owner.RefuseScripts && method.Name == nameof(ITransaction.ScriptEvaluateAsync) && args[0] is string)
                {
                    refused = true;
                    return Task.FromException<RedisResult>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM this user has no permissions to run the 'eval' command"));
                }

                if (method.Name == nameof(ITransaction.ExecuteAsync) && args is [CommandFlags flags])
                {
                    return refused
                        ? Task.FromException<bool>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "EXECABORT Transaction discarded because of previous errors."))
                        : ExecuteAsync(real, flags, pending);
                }

                return InterceptingProxy.Proceed;
            });
        }

        private async Task<bool> ExecuteAsync(ITransaction real, CommandFlags flags, List<Action<Exception>> pending)
        {
            var number = Interlocked.Increment(ref owner._execs);
            if (owner.LoseBeforeApply?.Invoke(number) == true)
            {
                var lost = new RedisTimeoutException(CommandFlags.None, "The EXEC got no reply.", CommandStatus.Sent);
                foreach (var fail in pending)
                {
                    fail(lost);
                }

                throw lost;
            }

            var committed = await real.ExecuteAsync(flags);
            return owner.LoseReply?.Invoke(number) == true
                ? throw new RedisTimeoutException(CommandFlags.None, "The reply of EXEC was lost.", CommandStatus.Sent)
                : committed;
        }

        private IConnectionMultiplexer HookedMultiplexer(IConnectionMultiplexer real) =>
            InterceptingProxy.Wrap(real, (method, args) =>
                method.Name == nameof(IConnectionMultiplexer.GetServer) && args is [EndPoint endpoint, ..] ? HookedServer(real.GetServer(endpoint)) : InterceptingProxy.Proceed);

        private IServer HookedServer(IServer real) =>
            InterceptingProxy.Wrap(real, (method, args) =>
            {
                // With a WAIT hook the master reports a replica, as the deployment these tests configure has one; the hook decides what it acknowledges.
                if (method.Name == nameof(IServer.RoleAsync) && owner.Wait is not null)
                {
                    return WithReplicaAsync(real);
                }

                if (method.Name == nameof(IServer.ExecuteAsync) && args is ["WAIT", ..] && (owner.Wait is not null || owner.BeforeWait is not null))
                {
                    return WaitAsync(real, (object[])args[1]!);
                }

                if (method.Name == nameof(IServer.ExecuteAsync) && args is ["INFO", ..] && owner.Info is { } rewrite)
                {
                    return RewrittenInfoAsync(real, rewrite);
                }

                return InterceptingProxy.Proceed;
            });

        private async Task<RedisResult> WaitAsync(IServer real, object[] wait)
        {
            var number = Interlocked.Increment(ref owner._waits);
            if (owner.BeforeWait is { } before)
            {
                await before(Endpoint, real);
            }

            return owner.Wait?.Invoke(Endpoint, number) is { } answer
                ? RedisResult.Create((RedisValue)answer)
                : await real.ExecuteAsync("WAIT", wait);
        }

        private async Task<RedisResult> RewrittenInfoAsync(IServer real, Func<EndPoint, string, string> rewrite) =>
            RedisResult.Create((RedisValue)rewrite(Endpoint, (string)(await real.ExecuteAsync("INFO"))!));

        private IDatabase RaceControl(Func<Task> after)
        {
            var fired = 0;
            return InterceptingProxy.Wrap(channel.ControlDatabase, (method, args) =>
                method.Name == nameof(IDatabase.HashGetAsync) && method.ReturnType == typeof(Task<RedisValue[]>) && Interlocked.Exchange(ref fired, 1) == 0
                    ? ReadThenAct(channel.ControlDatabase, method, args, after)
                    : InterceptingProxy.Proceed);
        }
    }
}
