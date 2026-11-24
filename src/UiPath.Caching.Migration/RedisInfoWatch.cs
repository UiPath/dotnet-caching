using Microsoft.Extensions.Options;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>Reads <c>INFO</c> of every primary and replica, turns it into CPU and lag, and runs the memory rules, once per interval.</summary>
internal sealed class RedisInfoWatch : IMoverMemory
{
    // The most INFO calls one reading has in flight.
    private const int MaxConcurrentReads = 4;

    private readonly Func<CancellationToken, Task<IReadOnlyList<NodeInfo>>> _read;
    private readonly LastGoodOptions _options;
    private readonly TimeProvider _time;
    private readonly MemoryRules _rules = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();
    private readonly Dictionary<string, (double Cpu, DateTimeOffset At)> _cpu = new(StringComparer.Ordinal);
    private InfoReading? _last;
    private Exception? _lastError;
    private DateTimeOffset _lastAt;

    /// <param name="read">Reads the nodes; <see cref="Over"/> builds the one that reads a connection.</param>
    public RedisInfoWatch(Func<CancellationToken, Task<IReadOnlyList<NodeInfo>>> read, IOptionsMonitor<KeyMoveMoverOptions> options, TimeProvider time)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _options = LastGoodOptions.Over(options);
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>A watch that reads <c>INFO</c> from the primary and the replica of every scan unit.</summary>
    public static RedisInfoWatch Over(IScanUnits units, IOptionsMonitor<KeyMoveMoverOptions> options, TimeProvider time) =>
        new(Reader(units), options, time);

    /// <summary>The read of every primary and replica of the units, in the order the units list them.</summary>
    public static Func<CancellationToken, Task<IReadOnlyList<NodeInfo>>> Reader(IScanUnits units) => ct => ReadUnitsAsync(units, ct);

    /// <summary>The latest reading, or a new one when it is older than the interval; a failed read is kept for the interval too.</summary>
    public async ValueTask<InfoReading> ReadAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (Cached(options) is { } fresh)
        {
            return fresh;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Cached(options) is { } again)
            {
                return again;
            }

            try
            {
                var nodes = await _read(cancellationToken).ConfigureAwait(false);
                var now = _time.GetUtcNow();
                var loads = Loads(nodes, now);
                var reading = new InfoReading(loads, _rules.Evaluate(nodes, now, options));
                lock (_state)
                {
                    (_last, _lastError, _lastAt) = (reading, null, now);
                }

                return reading;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OperationCanceledException && ex is not OutOfMemoryException)
            {
                lock (_state)
                {
                    (_last, _lastError, _lastAt) = (null, ex, _time.GetUtcNow());
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<string?> PauseReasonAsync(CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken).ConfigureAwait(false)).MemoryPause;

    private static async Task<IReadOnlyList<NodeInfo>> ReadUnitsAsync(IScanUnits units, CancellationToken cancellationToken)
    {
        // All nodes are read together, so the time limit of a reading covers the slowest node, not their sum.
        // At most a few INFO calls are in flight, so a large topology does not open a burst on the connection.
        using var limit = new SemaphoreSlim(MaxConcurrentReads);
        var reads = new List<Task<NodeInfo>>();
        foreach (var unit in await units.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            reads.Add(InfoAsync(limit, unit.Id, false, string.Empty, unit.Primary, cancellationToken));
            foreach (var replica in unit.ReplicasToJudge)
            {
                // A replica the topology names but the connection cannot reach counts as down, not as absent.
                reads.Add(replica.Server is { IsConnected: true } server ? InfoAsync(limit, unit.Id, true, replica.Name, server, cancellationToken) : Task.FromResult(NodeInfo.Down(unit.Id, replica.Name)));
            }
        }

        return await Task.WhenAll(reads).ConfigureAwait(false);
    }

    private static async Task<NodeInfo> InfoAsync(SemaphoreSlim limit, string unit, bool isReplica, string node, IServer server, CancellationToken cancellationToken)
    {
        await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A refusal (an ACL that denies INFO) or an incomplete reply throws, which the rate reads as red.
            var reply = await server.ExecuteAsync("INFO").WaitAsync(cancellationToken).ConfigureAwait(false);
            return NodeInfo.Parse(unit, isReplica, (string?)reply ?? string.Empty, node);
        }
        finally
        {
            limit.Release();
        }
    }

    // The reading or the failure of the last interval, or null when a new read is due.
    private InfoReading? Cached(KeyMoveMoverOptions options)
    {
        InfoReading? last;
        Exception? error;
        DateTimeOffset at;
        lock (_state)
        {
            (last, error, at) = (_last, _lastError, _lastAt);
        }

        if ((last is null && error is null) || _time.GetUtcNow() - at >= options.EvaluationInterval)
        {
            return null;
        }

        return last ?? throw new InvalidOperationException("INFO could not be read: " + error!.Message, error);
    }

    private List<NodeLoad> Loads(IReadOnlyList<NodeInfo> nodes, DateTimeOffset now)
    {
        var loads = new List<NodeLoad>();
        foreach (var node in nodes)
        {
            var id = node.Unit + (node.IsReplica ? "/r:" + node.Node : "/p");
            double? percent = null;

            // A replica that could not be reached has no reading to compare.
            var reached = node.Policy.Length > 0;
            if (reached && _cpu.TryGetValue(id, out var before) && now > before.At && node.CpuSeconds >= before.Cpu)
            {
                percent = (node.CpuSeconds - before.Cpu) * 100 / (now - before.At).TotalSeconds;
            }

            if (reached)
            {
                _cpu[id] = (node.CpuSeconds, now);
            }

            long? lag = null;
            if (node.IsReplica && node.ReplOffset is { } own && nodes.FirstOrDefault(n => !n.IsReplica && n.Unit == node.Unit)?.ReplOffset is { } primary)
            {
                lag = Math.Max(0, primary - own);
            }

            loads.Add(new NodeLoad(node.Unit, node.IsReplica, percent, lag, node.LinkUp == false));
        }

        return loads;
    }
}
