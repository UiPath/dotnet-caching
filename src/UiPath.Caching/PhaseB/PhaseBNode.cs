using System.Collections.Concurrent;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>
/// The Phase B parts of one node: the snapshot loop, the stage flag poller, the acknowledgements, the lease of each group, the leader's driver and the control upkeep. The advert tokens exist from construction;
/// everything that needs Redis starts with <see cref="StartAsync"/> and stops with <see cref="StopAsync"/>. A setup with no group runs nothing.
/// </summary>
internal sealed class PhaseBNode
{
    private readonly PhaseBSetup _setup;
    private readonly TimeProvider _time;
    private readonly DeferredStateReader _reader = new();
    private readonly EpochGate _gate = new();
    private readonly GroupStateMonitor _monitor;
    private readonly StageFlagPoller _poller;
    private readonly ConcurrentDictionary<string, long> _recycled = new(StringComparer.Ordinal);
    private Action _recycle = () => { };
    private CancellationTokenSource? _stop;
    private Task[] _loops = [];
    private StageDriver? _driver;

    public PhaseBNode(PhaseBSetup setup, TimeProvider time)
    {
        _setup = setup ?? throw new ArgumentNullException(nameof(setup));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _monitor = new GroupStateMonitor(_reader, setup.State, time, (group, code) => setup.Alert?.Invoke(group, code, string.Empty));
        _poller = new StageFlagPoller(setup.Stages, IsActive, setup.State, time);
        Tokens = new PhaseBAdvertTokens(
            setup.Groups,
            group => setup.Groups.FirstOrDefault(g => g.Name == group)?.CacheIds ?? [],
            _poller,
            _monitor,
            _gate,
            _monitor,
            group => _recycled.TryGetValue(group, out var ep) ? ep : null);
    }

    public PhaseBAdvertTokens Tokens { get; }

    public GroupStateMonitor Monitor => _monitor;

    public EpochGate Gate => _gate;

    /// <summary>Set once the node is started.</summary>
    public PhaseBRuntime? Runtime { get; private set; }

    /// <summary>True when the setup names a group, so the node has anything to run.</summary>
    public bool HasGroups => _setup.Groups.Count > 0;

    /// <param name="adverts">The router entries of the node hash; null starts the snapshot and the acknowledgements and no leader.</param>
    /// <param name="ownScope">The scope token of this node's adverts (G4).</param>
    public Task StartAsync(IRedisConnector connector, string connectorPrefix, IAdvertSource? adverts, string? ownScope, CancellationToken cancellationToken)
    {
        if (_stop is not null)
        {
            throw new InvalidOperationException("The Phase B node is already running; stop it before it is started again.");
        }

        if (!HasGroups)
        {
            return Task.CompletedTask;
        }

        var database = connector.Database;
        var control = connectorPrefix.Length == 0 ? database : database.WithKeyPrefix(connectorPrefix);
        var cluster = database.Multiplexer.GetServers().Any(s => s.ServerType == ServerType.Cluster);
        var version = connector.Version;
        var state = _setup.State;
        var names = new MoveKeyNames(connectorPrefix, _setup.LibraryPrefix, cluster);
        var stateScripts = new MoveScripts(database, true, version);
        var controlScripts = new MoveScripts(control, true, version);
        var topology = new MultiplexerTopology(database.Multiplexer, connectorPrefix, true, version, state.ExpectedReplicasPerMaster, barrierWait: state.BarrierWait);
        var sampler = new ControlSampler(control, names, topology);
        var store = new StateStore(database, names, stateScripts, sampler, _time);
        _reader.Attach(store);
        _recycle = _setup.RecycleConnections ?? connector.ForceReconnect;

        var report = (string kind, string detail) => _setup.Alert?.Invoke(string.Empty, kind, detail);
        var sequence = new TransitionSequence(topology, names, state, report);
        var records = new ControlRecordReader(control, names);
        var upkeep = new ControlUpkeep(records, sequence, store, state, _time, report);
        var scanner = new MasterScanner(connector.GetPrimaries, database.Database, connectorPrefix);
        var census = new MasterCensus(scanner, _setup.KeySpace, store, database, connectorPrefix, _time);
        var data = new GroupDataOps(scanner, _setup.KeySpace, store, stateScripts, controlScripts, names);
        var leases = _setup.Groups.ToDictionary(g => g.Name, g => new LeaderLease(stateScripts, names.Lead(g.Name), state, _time), StringComparer.Ordinal);
        var stuck = new StuckGateReopener(records, controlScripts, database, names, state, _time);
        var recovery = new StateRecovery(store, sampler, records, control, sequence, names, _time);
        var options = _setup.Driver;

        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _stop.Token;
        var names2 = _setup.Groups.Select(g => g.Name).ToArray();
        _monitor.Start(_setup.Groups, token);
        _poller.Start(names2, token);
        var acks = new AckCoordinator(_monitor, _gate, options, _time, BeforeAck);
        var loops = new List<Task>
        {
            Task.Run(() => acks.RunAsync(names2, token), CancellationToken.None),
            Task.Run(() => RunLeasesAsync(leases, token), CancellationToken.None),
        };
        if (adverts is not null)
        {
            _driver = new StageDriver(
                new StageDriverServices(store, stateScripts, names, sequence, new PreflightChecker(topology, state), new BarrierLedger(state, _time), adverts, _setup.Facts, _setup.Claim, data, new CensusMoverStatus(census, _setup.MoverFinishedPass), g => leases[g].HoldsLease, _monitor, ownScope, _setup.Alert, g => leases[g].Fence, upkeep),
                options,
                state,
                new FlagViews(options, _time),
                new ViewStability(_time),
                _time);
            _driver.Start(names2, token);
            loops.Add(Task.Run(() => RunUpkeepAsync(names2, leases, upkeep, stuck, store, token), CancellationToken.None));
        }

        _loops = [.. loops];
        Runtime = new PhaseBRuntime(
            store,
            new StageCommands(store, stateScripts, names, options),
            data,
            new ClearCoordinator(store, stateScripts, controlScripts, records, names, state, _time),
            new GroupRemoval(store, stateScripts, controlScripts, names, _setup.DropClaim),
            recovery,
            _driver!);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_stop is null)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_loops).ConfigureAwait(false);
        await _monitor.StopAsync().ConfigureAwait(false);
        await _poller.StopAsync().ConfigureAwait(false);
        if (_driver is not null)
        {
            await _driver.StopAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
        _stop = null;
        _driver = null;
        _loops = [];
        Runtime = null;
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

    private async Task RunLeasesAsync(Dictionary<string, LeaderLease> leases, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                foreach (var (group, lease) in leases)
                {
                    try
                    {
                        // A group the node has no reason to lead holds no lease, so a group that is not Phase B writes nothing.
                        if (_monitor.RulesFor(group).Relevant)
                        {
                            await lease.TryHoldAsync().ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is RedisException or TimeoutException)
                    {
                        // The lease stays valid only for what the last renewal bought.
                    }
                }

                await Task.Delay(_setup.State.LeaseRenewInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var lease in leases.Values)
            {
                await lease.ReleaseAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task RunUpkeepAsync(string[] groups, Dictionary<string, LeaderLease> leases, ControlUpkeep upkeep, StuckGateReopener stuck, StateStore store, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_setup.State.UpkeepInterval, _time, cancellationToken).ConfigureAwait(false);
                foreach (var group in groups.Where(g => leases[g].HoldsLease))
                {
                    try
                    {
                        await upkeep.CheckOnceAsync(group, cancellationToken, leases[group]).ConfigureAwait(false);
                        if (await store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is { } state)
                        {
                            var (reopened, stillClosed) = await stuck.CheckAsync(group, state, cancellationToken).ConfigureAwait(false);
                            foreach (var cache in stillClosed)
                            {
                                _setup.Alert?.Invoke(group, "gate_stuck", cache);
                            }

                            foreach (var cache in reopened)
                            {
                                _setup.Alert?.Invoke(group, "gate_reopened", cache);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is RedisException or TimeoutException)
                    {
                        _setup.Alert?.Invoke(group, "upkeep_failed", ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }
}
