using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The mover's scan with every step that writes taken out: reads the keys of a group and reports what a move would do with them; on the native side it only sorts the keys by ownership.</summary>
internal sealed class DryRunner
{
    private readonly string _group;
    private readonly IKeyMovePlane _plane;
    private readonly IGroupKeySpace _space;
    private readonly IReadOnlyDictionary<string, IMoveTarget> _targets;
    private readonly IScanUnits _units;
    private readonly string _connectorPrefix;
    private readonly IMoveRate _rate;
    private readonly TimeProvider _time;
    private readonly MoverOptions _moverOptions;
    private readonly DryRunOptions _options;
    private readonly KeyMoveMoverOptions _rates;
    private readonly MoveSide _side;

    public DryRunner(string group, IKeyMovePlane plane, IGroupKeySpace space, IReadOnlyDictionary<string, IMoveTarget> targets, IScanUnits units, string connectorPrefix, IMoveRate rate, TimeProvider time, MoverOptions? moverOptions = null, DryRunOptions? options = null, KeyMoveMoverOptions? rates = null, MoveSide side = MoveSide.Legacy)
    {
        _group = group ?? throw new ArgumentNullException(nameof(group));
        _plane = plane ?? throw new ArgumentNullException(nameof(plane));
        _space = space ?? throw new ArgumentNullException(nameof(space));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _units = units ?? throw new ArgumentNullException(nameof(units));
        _connectorPrefix = connectorPrefix ?? throw new ArgumentNullException(nameof(connectorPrefix));
        _rate = rate ?? throw new ArgumentNullException(nameof(rate));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _moverOptions = moverOptions ?? new MoverOptions();
        _options = options ?? new DryRunOptions();
        if (_options.DecodeCap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "DecodeCap must be non-negative.");
        }

        if (_options.SampleEvery < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SampleEvery must be at least 1.");
        }

        _rates = rates ?? new KeyMoveMoverOptions();
        _side = side;
    }

    public async Task<DryRunReport> RunAsync(CancellationToken cancellationToken)
    {
        var data = await ConnectedDataAsync(cancellationToken).ConfigureAwait(false);
        var units = await _units.ListAsync(cancellationToken).ConfigureAwait(false);
        var patterns = _space.Patterns(_group, _side);
        var run = new Run();
        foreach (var unit in units)
        {
            var server = unit.ScanServer;
            var before = run.Clone();
            try
            {
                await ScanUnitAsync(unit, server, data, patterns, run, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException && !ReferenceEquals(server, unit.Primary))
            {
                // A replica cursor means nothing on the primary, so the unit starts again there from zero, without the keys the replica pass counted.
                run = before;
                await ScanUnitAsync(unit, unit.Primary, data, patterns, run, cancellationToken).ConfigureAwait(false);
            }
        }

        return new DryRunReport(run.Caches, units.Count, run.Scanned, _rates, _side);
    }

    // A key with no owner or several is counted here and is not looked at further.
    private static bool CountUnowned(IReadOnlyList<string> owners, Run run)
    {
        if (owners.Count == 1)
        {
            return false;
        }

        run.Of(owners.Count == 0 ? string.Empty : string.Join('+', owners)).Count(owners.Count == 0 ? DryRunClasses.ExpectedUnmapped : DryRunClasses.Ambiguous);
        return true;
    }

    private async Task ScanUnitAsync(ScanUnit unit, IServer server, KeyMoveData data, IReadOnlyList<string> patterns, Run run, CancellationToken cancellationToken)
    {
        for (var index = 0; index < patterns.Count; index++)
        {
            await ScanPatternAsync(unit, server, data, patterns, index, run, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<KeyMoveData> ConnectedDataAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_plane.Data is { } data && data.IsConnected())
            {
                return data;
            }

            await Task.Delay(_moverOptions.PollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ScanPatternAsync(ScanUnit unit, IServer server, KeyMoveData data, IReadOnlyList<string> patterns, int index, Run run, CancellationToken cancellationToken)
    {
        var pattern = patterns[index];
        var earlier = patterns.Take(index).Where(p => GlobPatterns.MayOverlap(p, pattern)).ToArray();
        var cursor = "0";
        do
        {
            await WaitWhilePausedAsync(unit, cancellationToken).ConfigureAwait(false);
            var reply = await ScanAsync(server, data, cursor, pattern, cancellationToken).ConfigureAwait(false);
            var keys = ReadScanReply(reply, out cursor);
            foreach (var key in keys.Where(key => !earlier.Any(p => GlobPatterns.Matches(p, key))))
            {
                run.Scanned++;
                await InventoryKeyAsync(unit, key, run, cancellationToken).ConfigureAwait(false);
            }

            if (cursor != "0" || index + 1 < patterns.Count)
            {
                await Task.Delay(_moverOptions.MinScanInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        while (cursor != "0");
    }

    private Task<RedisResult> ScanAsync(IServer server, KeyMoveData data, string cursor, string pattern, CancellationToken cancellationToken) =>
        server.ExecuteAsync(data.Db.Database, "SCAN", [cursor, "MATCH", MasterScanner.EscapeGlob(_connectorPrefix) + pattern, "COUNT", _moverOptions.ScanCount]).WaitAsync(cancellationToken);

    // The keys of one SCAN reply with the connector prefix taken off; a key without the prefix is not the mover's.
    private List<string> ReadScanReply(RedisResult reply, out string cursor)
    {
        var parts = ((RedisResult[]?)reply)!;
        cursor = ((string?)parts[0])!;
        var keys = new List<string>();
        foreach (var raw in ((RedisResult[]?)parts[1])!)
        {
            if ((string?)raw is { } text && text.StartsWith(_connectorPrefix, StringComparison.Ordinal))
            {
                keys.Add(text[_connectorPrefix.Length..]);
            }
        }

        return keys;
    }

    private async Task InventoryKeyAsync(ScanUnit unit, string key, Run run, CancellationToken cancellationToken)
    {
        await WaitWhilePausedAsync(unit, cancellationToken).ConfigureAwait(false);
        var owners = _space.OwnersOf(_group, _side, key);
        if (CountUnowned(owners, run))
        {
            return;
        }

        var cacheId = owners[0];
        var inventory = run.Of(cacheId);
        if (_space.KindOf(_group, cacheId) == KeyMoveKind.Hash)
        {
            inventory.Count(DryRunClasses.Hash);
        }
        else if (_side == MoveSide.Native)
        {
            var movable = _space.IsMovable(_group, _side, cacheId, key, null) && _space.CallerKeyOf(_group, _side, cacheId, key) is not null;
            inventory.Count(movable ? DryRunClasses.Movable : DryRunClasses.Unmappable);
        }
        else
        {
            await InventoryLegacyKeyAsync(unit, key, cacheId, inventory, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InventoryLegacyKeyAsync(ScanUnit unit, string key, string cacheId, CacheInventory inventory, CancellationToken cancellationToken)
    {
        if (!_targets.TryGetValue(cacheId, out var target))
        {
            inventory.Count(DryRunClasses.NoTarget);
        }
        else if (!_space.IsMovable(_group, MoveSide.Legacy, cacheId, key, null) || _space.CallerKeyOf(_group, MoveSide.Legacy, cacheId, key) is not { } caller)
        {
            inventory.Count(DryRunClasses.Unmappable);
        }
        else
        {
            await ProbeKeyAsync(unit, target, cacheId, caller, inventory, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProbeKeyAsync(ScanUnit unit, IMoveTarget target, string cacheId, string caller, CacheInventory inventory, CancellationToken cancellationToken)
    {
        var decode = inventory.ShouldDecode(_options);
        var probe = await target.ProbeAsync(caller, _space.MoveMinTtl(_group, cacheId), decode, cancellationToken).ConfigureAwait(false);
        inventory.Add(probe, decode);
        if (_rate.Reserve("dry:" + unit.Id, 1, probe.SourceBytes ?? 0) is { } delay && delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitWhilePausedAsync(ScanUnit unit, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _rate.PauseReasonAsync(unit.Id, cancellationToken).ConfigureAwait(false) is null)
            {
                return;
            }

            await Task.Delay(_moverOptions.PollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Run
    {
        private readonly Dictionary<string, CacheInventory> _caches = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, CacheInventory> Caches => _caches;

        public long Scanned { get; set; }

        public Run Clone()
        {
            var copy = new Run { Scanned = Scanned };
            foreach (var (cacheId, inventory) in _caches)
            {
                copy._caches[cacheId] = inventory.Clone();
            }

            return copy;
        }

        public CacheInventory Of(string cacheId)
        {
            if (!_caches.TryGetValue(cacheId, out var inventory))
            {
                _caches[cacheId] = inventory = new CacheInventory();
            }

            return inventory;
        }
    }
}
