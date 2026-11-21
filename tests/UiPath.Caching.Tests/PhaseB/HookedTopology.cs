using System.Net;
using StackExchange.Redis;
using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

// The real topology with seams: a test can change the slot map between two readings and decide what each WAIT returns.
internal sealed class HookedTopology(IStateTopology inner) : IStateTopology
{
    private int _reads;
    private int _opened;
    private int _waits;

    // Given the real layout and the number of the reading (from 1), the layout the controller sees.
    public Func<ClusterLayout, int, ClusterLayout>? Layout { get; set; }

    // The result of a WAIT (endpoint, number of the WAIT from 1); null runs the real command.
    public Func<EndPoint, int, long?>? Wait { get; set; }

    public int Opened => Volatile.Read(ref _opened);

    public int Waits => Volatile.Read(ref _waits);

    public async Task<ClusterLayout> ReadLayoutAsync(CancellationToken cancellationToken)
    {
        var real = await inner.ReadLayoutAsync(cancellationToken);
        return Layout is { } hook ? hook(real, Interlocked.Increment(ref _reads)) : real;
    }

    public async Task<IMasterChannel> OpenAsync(EndPoint master, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _opened);
        return new HookedChannel(await inner.OpenAsync(master, cancellationToken), this);
    }

    private sealed class HookedChannel(IMasterChannel channel, HookedTopology owner) : IMasterChannel
    {
        public EndPoint Endpoint => channel.Endpoint;

        public MoveScripts Control => channel.Control;

        public IDatabase ControlDatabase => channel.ControlDatabase;

        public MoveScripts State => channel.State;

        public IDatabase StateDatabase => channel.StateDatabase;

        public async Task<long> WaitAsync(int replicas, TimeSpan timeout, CancellationToken cancellationToken) =>
            owner.Wait?.Invoke(Endpoint, Interlocked.Increment(ref owner._waits)) ?? await channel.WaitAsync(replicas, timeout, cancellationToken);

        public ValueTask DisposeAsync() => channel.DisposeAsync();
    }
}
