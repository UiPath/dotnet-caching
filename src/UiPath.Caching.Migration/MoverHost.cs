using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>Runs and stops the leased scan units for one group on this node.</summary>
internal sealed class MoverHost
{
#if NET9_0_OR_GREATER
    private readonly Lock _lifetimeGate = new();
#else
    private readonly object _lifetimeGate = new();
#endif
    private readonly string _group;
    private readonly IKeyMovePlane _plane;
    private readonly IGroupKeySpace _space;
    private readonly IReadOnlyDictionary<string, IMoveTarget> _targets;
    private readonly IScanUnits _units;
    private readonly ThrottledAlerts _alerts;
    private readonly Func<CancellationToken, Task<StateRecord?>> _readState;
    private readonly string _connectorPrefix;
    private readonly TimeProvider _time;
    private readonly MoverOptions _options;
    private readonly IMoveRate _rate;
    private readonly Dictionary<string, (MoverUnitRunner Runner, CancellationTokenSource Stop, Task Task)> _running = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private bool _stopping;
    private (long MoveId, string Identity, string Direction)? _runningKey;

    /// <param name="readState">The stored state of the group, as the node's state store reads it.</param>
    /// <param name="rate">The one rate of the process (a singleton), which every host takes, so that the node's cap holds across groups.</param>
    public MoverHost(string group, IKeyMovePlane plane, IGroupKeySpace space, IEnumerable<IMoveTarget> targets, IScanUnits units, Func<CancellationToken, Task<StateRecord?>> readState, string connectorPrefix, TimeProvider time, IMoveRate rate, MoverOptions? options = null)
        : this(group, plane, space, (targets ?? throw new ArgumentNullException(nameof(targets))).ToDictionary(t => t.CacheId, StringComparer.Ordinal), units, readState, connectorPrefix, time, rate, options)
    {
    }

    /// <param name="targets">The targets by cache id, read live, so a cache bound after the start is moved too.</param>
    public MoverHost(string group, IKeyMovePlane plane, IGroupKeySpace space, IReadOnlyDictionary<string, IMoveTarget> targets, IScanUnits units, Func<CancellationToken, Task<StateRecord?>> readState, string connectorPrefix, TimeProvider time, IMoveRate rate, MoverOptions? options = null)
    {
        _group = group;
        _plane = plane ?? throw new ArgumentNullException(nameof(plane));
        _space = space ?? throw new ArgumentNullException(nameof(space));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _units = units ?? throw new ArgumentNullException(nameof(units));
        _readState = readState ?? throw new ArgumentNullException(nameof(readState));
        _connectorPrefix = connectorPrefix ?? throw new ArgumentNullException(nameof(connectorPrefix));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? new MoverOptions();
        _rate = rate ?? throw new ArgumentNullException(nameof(rate));
        _alerts = new ThrottledAlerts(a => _plane.Alert(a.Group, a.Code, a.Detail), time, _options.AlertEvery);
    }

    /// <summary>The scan units this host runs now.</summary>
    public IReadOnlyCollection<string> RunningUnits
    {
        get
        {
            lock (_running)
            {
                return [.. _running.Keys];
            }
        }
    }

    /// <summary>Test seam: overrides the clock for reconciliation polls.</summary>
    internal TimeProvider? PollClock { get; init; }

    public void Start()
    {
        lock (_lifetimeGate)
        {
            if (_stop is not null)
            {
                throw new InvalidOperationException("The mover is already running.");
            }

            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    public async Task StopAsync()
    {
        if (!TryBeginStop(out var stop, out var loop))
        {
            return;
        }

        await CancelAndDisposeWhenDoneAsync(stop, loop).ConfigureAwait(false);
    }

    // An S3 of either direction with dur=1 whose id every fresh node acknowledged (allAck equals the id), and only while this node acts under it.
    private static (long MoveId, string Identity, string Direction)? Allowed(StateRecord? state, GroupRules rules) =>
        state is { State: "S3", Direction: MoverPasses.Forward or MoverPasses.Reverse, Durable: true, MoveId: { } id } && state.AllAck == state.Id
        && rules is { Relevant: true, Acting: { } acting } && acting.Id == state.Id && acting.Direction == state.Direction
            ? (id, state.Id, state.Direction)
            : null;

    private static bool ActsMoving(GroupRules rules) => rules is { Relevant: true, Acting.Direction: MoverPasses.Forward or MoverPasses.Reverse };

    private static async Task RunUnitAsync(MoverUnitRunner runner, CancellationToken cancellationToken)
    {
        try
        {
            await runner.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private bool TryBeginStop([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CancellationTokenSource? stop, out Task loop)
    {
        lock (_lifetimeGate)
        {
            if (_stopping || _stop is not { } active || _loop is not { } running)
            {
                stop = null;
                loop = Task.CompletedTask;
                return false;
            }

            _stopping = true;
            stop = active;
            loop = running;
            return true;
        }
    }

    private async Task CancelAndDisposeWhenDoneAsync(CancellationTokenSource stop, Task loop)
    {
        var cancellation = stop.CancelAsync();
        try
        {
            await Task.WhenAll(cancellation, loop).ConfigureAwait(false);
        }
        catch
        {
            await cancellation.ConfigureAwait(false);
            await loop.ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_lifetimeGate)
            {
                _stop?.Dispose();
                _stop = null;
                _loop = null;
                _stopping = false;
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var clock = PollClock ?? _time;
        try
        {
            while (true)
            {
                try
                {
                    await ReconcileAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
                {
                    // The state or the topology could not be read now; the next look tries again.
                    _alerts.Raise(new KeyMoveAlert(_group, "mover_reconcile_failed", ex.GetType().Name));
                }

                await Task.Delay(_options.PollInterval, clock, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        finally
        {
            await StopUnitsAsync(_running.Keys.ToArray()).ConfigureAwait(false);
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var rules = _plane.RulesFor(_group);

        // The stored state is read only while the node's rules say a move acts, so a group with no move costs the server nothing.
        var state = ActsMoving(rules) ? await _readState(cancellationToken).ConfigureAwait(false) : null;
        var key = Allowed(state, rules);
        if (key != _runningKey)
        {
            await StopUnitsAsync(_running.Keys.ToArray()).ConfigureAwait(false);
            _runningKey = key;
        }

        if (key is not { } running)
        {
            return;
        }

        var units = await _units.ListAsync(cancellationToken).ConfigureAwait(false);
        var current = units.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        await StopUnitsAsync([.. _running.Where(r => !current.Contains(r.Key) || r.Value.Task.IsCompleted).Select(r => r.Key)]).ConfigureAwait(false);
        foreach (var unit in units.Where(u => !_running.ContainsKey(u.Id)))
        {
            var runner = new MoverUnitRunner(_group, unit, running.MoveId, running.Identity, ct => StillAsync(running, ct), _plane, _space, _targets, _connectorPrefix, _options, _rate, _time, _alerts, running.Direction);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var task = Task.Run(() => RunUnitAsync(runner, stop.Token), CancellationToken.None);
            lock (_running)
            {
                _running[unit.Id] = (runner, stop, task);
            }
        }
    }

    private async Task<bool> StillAsync((long MoveId, string Identity, string Direction) key, CancellationToken cancellationToken) =>
        Allowed(await _readState(cancellationToken).ConfigureAwait(false), _plane.RulesFor(_group)) == key;

    private async Task StopUnitsAsync(string[] ids)
    {
        foreach (var id in ids)
        {
            (MoverUnitRunner Runner, CancellationTokenSource Stop, Task Task) running;
            lock (_running)
            {
                if (!_running.Remove(id, out running))
                {
                    continue;
                }
            }

            await running.Stop.CancelAsync().ConfigureAwait(false);
            await running.Task.ConfigureAwait(false);
            running.Stop.Dispose();
        }
    }
}
