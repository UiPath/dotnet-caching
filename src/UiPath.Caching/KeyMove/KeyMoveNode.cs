using System.Collections.Concurrent;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using UiPath.Caching.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>The key move parts of one node: snapshot loop, flag poller, acknowledgements, leases, leader driver and control upkeep.</summary>
/// <remarks>The advert tokens exist from construction; everything that needs Redis starts with <c>StartAsync</c> and is rebuilt when the connection is replaced, which also restarts the driver, so its view and stability waits begin again and a <c>Runtime</c> an app kept goes stale. A setup with no group runs nothing.</remarks>
internal sealed class KeyMoveNode
{
    private static readonly TimeSpan ScanRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RewireCheckInterval = TimeSpan.FromMilliseconds(500);

    private readonly KeyMoveSetup _setup;
    private readonly TimeProvider _time;
    private readonly DeferredStateReader _reader = new();
    private readonly EpochGate _gate = new();
    private readonly GroupStateMonitor _monitor;
    private readonly StageFlagPoller _poller;
    private readonly BarrierLedger _ledger;
    private readonly ThrottledAlerts _ambiguity;
    private readonly ConcurrentDictionary<string, long> _recycled = new(StringComparer.Ordinal);
    private Action _recycle = () => { };
    private CancellationTokenSource? _stop;
    private Task[] _loops = [];
    private StageDriver? _driver;
    private IRedisConnector? _connector;
    private string _connectorPrefix = string.Empty;
    private IAdvertSource? _adverts;
    private string? _ownScope;
    private Dictionary<string, LeaderLease> _leases = [];
    private volatile Wiring? _wiring;
    private KeyMoveDataAccess? _access;

    public KeyMoveNode(KeyMoveSetup setup, TimeProvider time)
    {
        _setup = setup ?? throw new ArgumentNullException(nameof(setup));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _ledger = new BarrierLedger(setup.State, time);
        _ambiguity = new ThrottledAlerts(setup.Alert, time, setup.State.PollInterval);
        _monitor = new GroupStateMonitor(_reader, setup.State, time, (group, code) => setup.Alert?.Invoke(new KeyMoveAlert(group, code, string.Empty)));
        _poller = new StageFlagPoller(setup.Stages, IsActive, setup.State, time);
        Tokens = new KeyMoveAdvertTokens(
            setup.Groups,
            group => setup.Groups.FirstOrDefault(g => g.Name == group)?.CacheIds ?? [],
            _poller,
            _monitor,
            _gate,
            _monitor,
            group => _recycled.TryGetValue(group, out var ep) ? ep : null);
    }

    public KeyMoveAdvertTokens Tokens { get; }

    public GroupStateMonitor Monitor => _monitor;

    public EpochGate Gate => _gate;

    /// <summary>Set once the node is started.</summary>
    public KeyMoveRuntime? Runtime { get; private set; }

    /// <summary>True when the setup names a group, so the node has anything to run.</summary>
    public bool HasGroups => _setup.Groups.Count > 0;

    /// <summary>The scan units of the current connection, for the mover; null before the node is started.</summary>
    internal IScanUnits? ScanUnits => _wiring?.Units;

    /// <summary>The data view, scripts and key names of the current connection, for the router's data path; null before the node is started.</summary>
    internal KeyMoveDataAccess? DataAccess
    {
        get
        {
            if (_wiring is not { } w)
            {
                return null;
            }

            var held = _access;
            if (held is not null && ReferenceEquals(held.Scripts, w.ControlScripts))
            {
                return held;
            }

            _access = new KeyMoveDataAccess(w.Control, w.ControlScripts, w.Names, w.Mux);
            return _access;
        }
    }

    /// <param name="adverts">The router entries of the node hash; null starts the snapshot and the acknowledgements and no leader.</param>
    /// <param name="ownScope">The scope token of this node's adverts.</param>
    public async Task StartAsync(IRedisConnector connector, string connectorPrefix, IAdvertSource? adverts, string? ownScope, CancellationToken cancellationToken)
    {
        if (_stop is not null)
        {
            throw new InvalidOperationException("The key move node is already running; stop it before it is started again.");
        }

        if (!HasGroups)
        {
            return;
        }

        _connector = connector;
        _connectorPrefix = connectorPrefix;
        _adverts = adverts;
        _ownScope = ownScope;
        _recycle = _setup.RecycleConnections ?? connector.ForceReconnect;
        await connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var wiring = Wire();
        _leases = _setup.Groups.ToDictionary(g => g.Name, g => new LeaderLease(wiring.StateScripts, wiring.Names.Lead(g.Name), _setup.State, _time), StringComparer.Ordinal);
        _wiring = wiring;
        _reader.Attach(wiring.Store);

        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _stop.Token;
        var names = _setup.Groups.Select(g => g.Name).ToArray();
        _monitor.Start(_setup.Groups, token);
        _poller.Start(names, token);
        var acks = new AckCoordinator(_monitor, _gate, _setup.Driver, _time, BeforeAck);
        var loops = new List<Task>
        {
            Task.Run(() => acks.RunAsync(names, token), CancellationToken.None),
            Task.Run(() => RunLeasesAsync(token), CancellationToken.None),
            Task.Run(() => RunRewireAsync(names, token), CancellationToken.None),
        };
        if (adverts is not null)
        {
            StartDriver(wiring, names, token);
            loops.Add(Task.Run(() => RunUpkeepAsync(names, token), CancellationToken.None));
        }

        _loops = [.. loops];
        Runtime = RuntimeOf(wiring);
    }

    public async Task StopAsync()
    {
        var stop = _stop;
        if (stop is null)
        {
            return;
        }

        try
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        finally
        {
            // Whatever the loops threw, every part is stopped and the node can be started again.
            try
            {
                await _monitor.StopAsync().ConfigureAwait(false);
                await _poller.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (_driver is not null)
                    {
                        await _driver.StopAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    stop.Dispose();
                    _stop = null;
                    _driver = null;
                    _loops = [];
                    Runtime = null;
                }
            }
        }
    }

    // Everything that holds a connection, built over the connector's current one; rebuilt when the connector replaces it.
    private Wiring Wire()
    {
        var connector = _connector!;
        var prefix = _connectorPrefix;
        var database = connector.Database;
        var control = prefix.Length == 0 ? database : database.WithKeyPrefix(prefix);
        var cluster = database.Multiplexer.GetServers().Any(s => s.ServerType == ServerType.Cluster);
        var version = connector.Version;
        var state = _setup.State;
        var names = new MoveKeyNames(prefix, _setup.LibraryPrefix, cluster);
        var stateScripts = new MoveScripts(database, true, version);
        var controlScripts = new MoveScripts(control, true, version);
        var topology = new MultiplexerTopology(database.Multiplexer, prefix, true, version, state.ExpectedReplicasPerMaster, barrierWait: state.BarrierWait);
        var sampler = new ControlSampler(control, names, topology);
        var store = new StateStore(database, names, stateScripts, sampler, _time);
        void Report(string kind, string detail) => _setup.Alert?.Invoke(new KeyMoveAlert(string.Empty, kind, detail));
        var sequence = new TransitionSequence(topology, names, state, Report, _time);
        var records = new ControlRecordReader(control, names);
        var units = new MultiplexerScanUnits(() => database.Multiplexer);
        var scanner = new MasterScanner(connector.GetPrimaries, topology, database.Database, prefix, _time, ScanRetryDelay, alert: _setup.Alert);
        return new Wiring(
            database.Multiplexer,
            store,
            stateScripts,
            controlScripts,
            names,
            topology,
            sequence,
            records,
            new ControlUpkeep(records, sequence, store, state, _time, Report),
            new GroupDataOps(scanner, _setup.KeySpace, store, stateScripts, controlScripts, names, alert: _ambiguity.Raise),
            new KeyInventory(scanner, _setup.KeySpace, store, database, prefix, _time, _ambiguity.Raise),
            new StuckGateReopener(records, controlScripts, database, names, state, _time),
            new MarkerSweeper(control, controlScripts, names, state, _time),
            new RetireGate(control, names, state, _time),
            new StateRecovery(store, store, records, sequence, _time),
            new ClearCoordinator(store, stateScripts, controlScripts, records, names, state, _time),
            new KeyMoveSweep(scanner, _setup.KeySpace, control, names),
            new GroupRemoval(store, stateScripts, controlScripts, names, _setup.DropClaim),
            control,
            units,
            new MoverPasses(units, control, names, store.ReadAsync, _setup.KeySpace.HasHashCache, _ambiguity.Raise));
    }

    private void StartDriver(Wiring wiring, string[] names, CancellationToken token)
    {
        _driver = new StageDriver(
            new StageDriverServices(
                wiring.Store,
                wiring.StateScripts,
                wiring.Names,
                wiring.Sequence,
                new PreflightChecker(wiring.Topology, _setup.State),
                _ledger,
                _adverts!,
                _setup.Facts,
                _setup.Claim,
                wiring.Data,
                new InventoryMoverStatus(wiring.Inventory, _time, _setup.MoverFinishedPass ?? wiring.Passes.FinishedAfterAckAsync, _setup.State.ReplicaLagMax, _setup.MoverFinishedPass is null ? wiring.Passes.EvidenceAsync : null),
                g => _leases[g].HoldsLease,
                _monitor,
                _ownScope,
                _setup.Alert,
                g => _leases[g].Fence,
                wiring.Upkeep,
                wiring.Retire,
                wiring.Sweeper),
            _setup.Driver,
            _setup.State,
            new FlagViews(_setup.Driver, _time),
            new ViewStability(_time),
            _time);
        _driver.Start(names, token);
    }

    private KeyMoveRuntime RuntimeOf(Wiring w) =>
        new(w.Store, new StageCommands(w.Store, w.StateScripts, w.Names, _setup.Driver), w.Data, w.Clear, w.Sweep, w.Removal, w.Recovery, _driver);

    // The connector replaces its connection after a failure and on a forced reconnect, and disposes the old one: the services are rebuilt over the new one, keeping the leases and their tokens.
    private async Task RunRewireAsync(string[] names, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(RewireCheckInterval, _time, cancellationToken).ConfigureAwait(false);
                try
                {
                    await _connector!.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    if (!ReferenceEquals(_connector.Database.Multiplexer, _wiring!.Mux))
                    {
                        await RewireAsync(names).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The connection is not up yet, or the services could not start over it; the next check looks again.
                    _setup.Alert?.Invoke(new KeyMoveAlert(string.Empty, "rewire_failed", ex.Message));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private async Task RewireAsync(string[] names)
    {
        var wiring = Wire();
        _reader.Attach(wiring.Store);
        foreach (var lease in _leases.Values)
        {
            lease.Rebind(wiring.StateScripts);
        }

        if (_driver is not null)
        {
            await _driver.StopAsync().ConfigureAwait(false);
            StartDriver(wiring, names, _stop!.Token);
        }

        // Set last, so a restart that failed is tried again: the connection still differs from the one wired.
        _wiring = wiring;
        Runtime = RuntimeOf(wiring);
    }

    // The flag is read every 2 s from S1 to S4 and while a listed group waits in S0 or is unknown; a closed group, and a group the node does not list, is read every minute.
    private bool IsActive(string group)
    {
        var rules = _monitor.RulesFor(group);
        if (!rules.Relevant)
        {
            return false;
        }

        var stage = rules.Acting?.State;
        return stage is "S1" or "S2" or "S3" or "S4" || (stage is null or "S0" && _setup.Groups.Any(g => g.Name == group && g.Listed));
    }

    // Before a node acknowledges the S0 of X11 it recycles its data connections, once for that identity, and advertises rc.
    private bool BeforeAck(string group)
    {
        if (_monitor.CurrentState(group) is not { State: "S0", Durable: true } state || state.Field(StateFields.ColdStart).Length == 0)
        {
            return true;
        }

        if (!_recycled.TryGetValue(group, out var ep) || ep != state.Ep)
        {
            _recycle();
            _recycled[group] = state.Ep;
        }

        return true;
    }

    private async Task RunLeasesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                foreach (var (group, lease) in _leases)
                {
                    try
                    {
                        // A group the node has no reason to lead holds no lease, so a group that is not key move writes nothing.
                        if (_monitor.RulesFor(group).Relevant)
                        {
                            await lease.TryHoldAsync().ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The lease stays valid only for what the last renewal bought.
                    }
                }

                await Task.Delay(_setup.State.LeaseRenewInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var lease in _leases.Values)
            {
                try
                {
                    await lease.ReleaseAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The lease then lapses by its own lifetime; a failed release must not keep the node from stopping.
                    _ = ex;
                }
            }
        }
    }

    private async Task RunUpkeepAsync(string[] groups, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_setup.State.UpkeepInterval, _time, cancellationToken).ConfigureAwait(false);
                foreach (var group in groups.Where(g => _leases[g].HoldsLease))
                {
                    try
                    {
                        await CheckUpkeepAsync(group, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _setup.Alert?.Invoke(new KeyMoveAlert(group, "upkeep_failed", ex.Message));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private async Task CheckUpkeepAsync(string group, CancellationToken cancellationToken)
    {
        var wiring = _wiring!;
        await wiring.Upkeep.CheckOnceAsync(group, cancellationToken, _leases[group]).ConfigureAwait(false);
        if (await wiring.Store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is { } state)
        {
            var (reopened, stillClosed) = await wiring.Stuck.CheckAsync(group, state, cancellationToken).ConfigureAwait(false);
            foreach (var cache in stillClosed)
            {
                _setup.Alert?.Invoke(new KeyMoveAlert(group, "gate_stuck", cache));
            }

            foreach (var cache in reopened)
            {
                _setup.Alert?.Invoke(new KeyMoveAlert(group, "gate_reopened", cache));
            }

            var swept = await wiring.Sweeper.SweepAsync(group, state, SweepMode.Orphans, cancellationToken).ConfigureAwait(false);
            if (swept.DidWork)
            {
                _setup.Alert?.Invoke(new KeyMoveAlert(group, "marker_sweep", $"obsolete={swept.Obsolete};helped={swept.Helped};orphans={swept.Orphans}"));
            }
        }
    }

    private sealed record Wiring(
        IConnectionMultiplexer Mux,
        StateStore Store,
        MoveScripts StateScripts,
        MoveScripts ControlScripts,
        MoveKeyNames Names,
        MultiplexerTopology Topology,
        TransitionSequence Sequence,
        ControlRecordReader Records,
        ControlUpkeep Upkeep,
        GroupDataOps Data,
        KeyInventory Inventory,
        StuckGateReopener Stuck,
        MarkerSweeper Sweeper,
        RetireGate Retire,
        StateRecovery Recovery,
        ClearCoordinator Clear,
        KeyMoveSweep Sweep,
        GroupRemoval Removal,
        IDatabase Control,
        MultiplexerScanUnits Units,
        MoverPasses Passes);
}
