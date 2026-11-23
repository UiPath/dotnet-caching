using System.Net;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

// The real topology with seams: a test can change the slot map between two readings and decide what each WAIT returns.
internal sealed class HookedTopology(IStateTopology inner) : IStateTopology
{
    private int _reads;
    private int _opened;
    private int _waits;
    private int _controlCalls;
    private int _stateCalls;

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

    public int Opened => Volatile.Read(ref _opened);

    public int Waits => Volatile.Read(ref _waits);

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
        return new HookedChannel(await inner.OpenAsync(master, cancellationToken), this);
    }

    private sealed class HookedChannel(IMasterChannel channel, HookedTopology owner) : IMasterChannel
    {
        public EndPoint Endpoint => channel.Endpoint;

        public MoveScripts Control => channel.Control;

        public IDatabase ControlDatabase => owner.AfterControlRead is { } after ? RaceControl(after) : channel.ControlDatabase;

        public MoveScripts State => channel.State;

        public IDatabase StateDatabase => channel.StateDatabase;

        public async Task<EndPoint?> ControlOwnerAsync(RedisKey key) =>
            owner.ControlOwner?.Invoke(Endpoint, key, Interlocked.Increment(ref owner._controlCalls)) ?? await channel.ControlOwnerAsync(key);

        public async Task<EndPoint?> StateOwnerAsync(RedisKey key) =>
            owner.StateOwner?.Invoke(Endpoint, key, Interlocked.Increment(ref owner._stateCalls)) ?? await channel.StateOwnerAsync(key);

        public async Task<long> WaitAsync(int replicas, TimeSpan timeout, CancellationToken cancellationToken) =>
            owner.Wait?.Invoke(Endpoint, Interlocked.Increment(ref owner._waits)) ?? await channel.WaitAsync(replicas, timeout, cancellationToken);

        public ValueTask DisposeAsync() => channel.DisposeAsync();

        private static async Task<RedisValue[]> ReadThenAct(IDatabase real, System.Reflection.MethodInfo method, object?[] args, Func<Task> after)
        {
            var read = await (Task<RedisValue[]>)method.Invoke(real, args)!;
            await after();
            return read;
        }

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
