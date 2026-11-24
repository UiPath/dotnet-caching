using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>Processes one scan unit under its lease and saves its cursor after each batch.</summary>
internal sealed class MoverUnitRunner
{
    private const long EndBatch = -2;
    private const long RetryOom = -1;
    private const CommandFlags Master = CommandFlags.DemandMaster;
    private const string Unmappable = "unmappable";

    // A pace that has lasted more than this many polls is shown as a pause, so a unit that waits for minutes is not silent.
    private const int PacingReportedAfterSlices = 3;

    // The classes a pass accepts: a key in one of them is a settled answer, not a failure to move it.
    private static readonly HashSet<string> Accepted = ["expired", "too_large", "unreadable", Unmappable, "short_ttl", "duplicate", "ambiguous"];

    private readonly string _group;
    private readonly ScanUnit _unit;
    private readonly long _moveId;
    private readonly string _identity;
    private readonly Func<CancellationToken, Task<bool>> _stillAllowed;
    private readonly IKeyMovePlane _plane;
    private readonly IGroupKeySpace _space;
    private readonly IReadOnlyDictionary<string, IMoveTarget> _targets;
    private readonly string _connectorPrefix;
    private readonly MoverOptions _options;
    private readonly IMoveRate _rate;
    private readonly TimeProvider _time;
    private readonly ThrottledAlerts _alerts;
    private readonly string _token = MoveScripts.NewNonce();
    private long _renewedAt;
    private bool _alertedAmbiguity;

    public MoverUnitRunner(string group, ScanUnit unit, long moveId, string identity, Func<CancellationToken, Task<bool>> stillAllowed, IKeyMovePlane plane, IGroupKeySpace space, IReadOnlyDictionary<string, IMoveTarget> targets, string connectorPrefix, MoverOptions options, IMoveRate rate, TimeProvider time, ThrottledAlerts alerts)
    {
        (_group, _unit, _moveId, _identity, _stillAllowed, _plane, _space, _targets, _connectorPrefix, _options, _rate, _time, _alerts) = (group, unit, moveId, identity, stillAllowed, plane, space, targets, connectorPrefix, options, rate, time, alerts);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        RouterTelemetry.AddMoverUnits(1, _group);
        KeyMoveData? held = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_plane.Data is not { } data || !data.IsConnected())
                    {
                        await Task.Delay(_options.PollInterval, _time, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    held = data;

                    // Null when the pass ended without finishing, whether the lease is another runner's or the state moved on: look again soon.
                    var wait = await RunPassAsync(data, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(wait ?? _options.PollInterval, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
                {
                    _alerts.Raise(new KeyMoveAlert(_group, "mover_unit_failed", $"{_unit.Id}: {ex.GetType().Name}"));
                    await Task.Delay(_options.PollInterval, _time, cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            RouterTelemetry.AddMoverUnits(-1, _group);
            if (held is not null)
            {
                try
                {
                    await held.Scripts.LeaseReleaseAsync(held.Names.UnitLease(_group, _unit.Id), _token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // The lease lapses by its own lifetime; a failed release must not keep the unit from stopping.
                    _ = ex;
                }
            }
        }
    }

    internal static string HashOf(IReadOnlyList<string> patterns) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', patterns))))[..16];

    private static bool SameEndpoint(string source, IServer server) =>
        source.StartsWith(server.EndPoint.ToString() + "#", StringComparison.Ordinal);

    // The endpoint and the run id of the server: a restart or a failover that keeps the endpoint changes the run id, and a cursor of the old run is of no use.
    // Null when the run id is not known, since a cursor can never be bound to a server that cannot be told from its restart.
    private static async Task<string?> SourceOfAsync(IServer server)
    {
        string? runId;
        try
        {
            runId = (await server.InfoAsync("server").ConfigureAwait(false)).SelectMany(g => g).FirstOrDefault(e => e.Key == "run_id").Value;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return null;
        }

        return string.IsNullOrEmpty(runId) ? null : $"{server.EndPoint}#{runId}";
    }

    // One pass of the unit from its stored cursor to the cursor's return to zero. Returns how long to wait before the next pass, or null when the pass ended without finishing.
    private async Task<TimeSpan?> RunPassAsync(KeyMoveData data, CancellationToken cancellationToken)
    {
        // A pass starts only while the state is a forward S3 that every node acknowledged, which is read again before every batch.
        if (!await _stillAllowed(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var names = data.Names;
        var pass = new PassKeys(data, names.UnitLease(_group, _unit.Id), names.UnitCursor(_group, _unit.Id, MoverPasses.Forward, _moveId), names.UnitPass(_group, _unit.Id, MoverPasses.Forward, _moveId), (long)_options.LeaseTtl.TotalMilliseconds);
        var sent = _time.GetTimestamp();
        var code = await data.Scripts.LeaseAsync(pass.Lease, _token, pass.LeaseMs).ConfigureAwait(false);
        if (code == MoveScripts.LeaseHeldByAnother)
        {
            return null;
        }

        _renewedAt = sent;
        var patterns = _space.Patterns(_group, MoveSide.Legacy);
        var (cursor, wait) = await OpenCursorAsync(pass, code, HashOf(patterns)).ConfigureAwait(false);
        if (cursor is null)
        {
            return wait;
        }

        _alertedAmbiguity = false;
        var ownership = new PassOwnership(this, data, pass.Lease, pass.LeaseMs);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The state moved on (a forward step, a back move, an abandon, any identity change), the acknowledgement is gone, or the lease is no longer this runner's: the pass stops here.
            if (!await _stillAllowed(cancellationToken).ConfigureAwait(false) || !await RenewAsync(data, pass.Lease, pass.LeaseMs).ConfigureAwait(false))
            {
                return null;
            }

            // Paused (health, memory, schedule): nothing is scanned or published, and the lease is kept alive meanwhile.
            if (!await WaitWhilePausedAsync(pass, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var scan = await ScanAsync(data, cursor, patterns, cancellationToken).ConfigureAwait(false);
            if (scan is null)
            {
                // No verified run id: the pass does not scan, the unit is free for a runner that can verify one, and the next look tries again.
                _alerts.Raise(new KeyMoveAlert(_group, "mover_unit_unverified", $"{_unit.Id}: the run id of the server to scan is unknown, so the pass waits"));
                await data.Scripts.LeaseReleaseAsync(pass.Lease, _token).ConfigureAwait(false);
                return null;
            }

            var (next, keys, scanned) = scan.Value;
            var tally = new Tally(_group);
            if (!await MoveBatchAsync(pass, keys, tally, ownership, cancellationToken).ConfigureAwait(false))
            {
                await FlushAsync(data, tally).ConfigureAwait(false);
                return null;
            }

            (cursor, wait) = await EndBatchAsync(pass, next, patterns.Count, scanned, tally).ConfigureAwait(false);
            if (cursor is null)
            {
                return wait;
            }

            await Task.Delay(_options.MinScanInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    // The cursor to scan from: the stored one when it belongs to this identity and pattern list, else a new pass. Null, with how long to wait, when no pass may start.
    private async Task<(ScanCursor? Cursor, TimeSpan? Wait)> OpenCursorAsync(PassKeys pass, long leaseCode, string patternsHash)
    {
        var stored = await pass.Data.Db.StringGetAsync(pass.Cursor, Master).ConfigureAwait(false);
        var resumed = ScanCursor.Parse((string?)stored) is { } found && found.Identity == _identity && found.Patterns == patternsHash ? found : null;
        if (resumed is null)
        {
            return await StartPassAsync(pass, ScanCursor.Start(_identity, string.Empty, patternsHash)).ConfigureAwait(false);
        }

        if (leaseCode == MoveScripts.LeaseAcquired && !resumed.IsStart)
        {
            // A lapsed lease with a cursor behind it: this runner continues another's pass.
            RouterTelemetry.CountMoverLeaseTakeover(_group);
            await AddProgressAsync(pass.Data, "lease_takeovers", 1).ConfigureAwait(false);
        }

        return (resumed, null);
    }

    private async Task<(ScanCursor? Cursor, TimeSpan? Wait)> StartPassAsync(PassKeys pass, ScanCursor cursor)
    {
        // Passes of a unit start at least PassPause apart, whichever runner holds it: the life of a key counts it, so no clock of another node does.
        var pace = pass.Data.Names.UnitPace(_group, _unit.Id, MoverPasses.Forward, _moveId);
        if (!await pass.Data.Db.StringSetAsync(pace, "1", _options.PassPause, When.NotExists, Master).ConfigureAwait(false))
        {
            var left = await pass.Data.Db.KeyTimeToLiveAsync(pace, Master).ConfigureAwait(false);
            await pass.Data.Scripts.LeaseReleaseAsync(pass.Lease, _token).ConfigureAwait(false);
            return (null, left ?? _options.PollInterval);
        }

        // The pass is stored as it starts, so a runner that dies before its first batch leaves a pass another can take, not a pace key that blocks the next.
        var step = await pass.Data.Scripts.UnitStepAsync(pass.Lease, pass.Cursor, pass.Pass, _token, pass.LeaseMs, cursor.Text, string.Empty, (long)_options.CursorLife.TotalMilliseconds, (long)_options.PassRecordLife.TotalMilliseconds).ConfigureAwait(false);
        return step == MoveScripts.UnitLeaseLost ? (null, null) : (cursor, null);
    }

    // Moves the keys of one batch. False when the pass must stop: the lease or the state was lost, or a key ended the batch.
    // The rate is charged before a key is captured: its key, and the bytes of the value published before it, which the promotion recorded after its publish.
    // Nothing waits while a fence or a marker is held, and the wait stays out of the mover's own timing.
    private async Task<bool> MoveBatchAsync(PassKeys pass, List<string> keys, Tally tally, PassOwnership ownership, CancellationToken cancellationToken)
    {
        var renewal = new RenewalCount();
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await PayAsync(pass, 1, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            ownership.NewKey();
            if (await MoveWithRetryAsync(pass, key, tally, ownership, renewal, cancellationToken).ConfigureAwait(false) == EndBatch)
            {
                return false;
            }
        }

        // The last value of the batch is paid before the cursor moves on.
        return await PayAsync(pass, 0, cancellationToken).ConfigureAwait(false);
    }

    // A key the server refused for memory is tried once more when the backoff has ended, through the same checks; the retry runs CAP again, which asks the ownership first.
    // The second refusal is counted and left for the next pass.
    private async Task<long> MoveWithRetryAsync(PassKeys pass, string key, Tally tally, PassOwnership ownership, RenewalCount renewal, CancellationToken cancellationToken)
    {
        var retryOnOom = true;
        while (true)
        {
            if (!await MayMoveAsync(pass, renewal, cancellationToken).ConfigureAwait(false))
            {
                return EndBatch;
            }

            var moved = await MoveKeyAsync(key, tally, ownership, retryOnOom, cancellationToken).ConfigureAwait(false);
            if (moved != RetryOom)
            {
                return moved;
            }

            retryOnOom = false;
        }
    }

    // The pause comes first, and ownership is judged after it, so a key is never scheduled on an ownership or identity that was lost while paused.
    private async Task<bool> MayMoveAsync(PassKeys pass, RenewalCount renewal, CancellationToken cancellationToken)
    {
        if (!await WaitWhilePausedAsync(pass, cancellationToken).ConfigureAwait(false) || !HoldsLease() || !ForwardS3())
        {
            return false;
        }

        if (!RenewalDue(++renewal.Keys))
        {
            return true;
        }

        renewal.Keys = 0;
        return await RenewAsync(pass.Data, pass.Lease, pass.LeaseMs).ConfigureAwait(false) && await _stillAllowed(cancellationToken).ConfigureAwait(false);
    }

    // Reserves the keys and the bytes the promotions have recorded since the last payment, and waits them out.
    private async Task<bool> PayAsync(PassKeys pass, int keys, CancellationToken cancellationToken)
    {
        var bytes = _rate.TakeDebt(_unit.Id);
        if (keys == 0 && bytes == 0)
        {
            return true;
        }

        return await PaceAsync(pass, _rate.Reserve(_unit.Id, keys, bytes), cancellationToken).ConfigureAwait(false);
    }

    // Waits out a reservation in poll-sized slices, renewing the lease and watching the state between them, so a long pace never outlives the lease; false when the unit must stop.
    // What is left is asked of the rate on each slice, so a reload that raises the rate shortens the wait, and a pause that begins during it is honoured.
    private async Task<bool> PaceAsync(PassKeys pass, TimeSpan delay, CancellationToken cancellationToken)
    {
        var slices = 0;
        while (delay > TimeSpan.Zero)
        {
            var slice = delay < _options.PollInterval ? delay : _options.PollInterval;
            await Task.Delay(slice, _time, cancellationToken).ConfigureAwait(false);
            delay = _rate.Remaining(_unit.Id) ?? delay - slice;
            if (++slices == PacingReportedAfterSlices + 1)
            {
                RouterTelemetry.CountMoverPaused("pacing");
            }

            // The state is judged after every slice, the last one too, and the lease is renewed while the wait goes on or when a third of its life is spent.
            if (!await _stillAllowed(cancellationToken).ConfigureAwait(false)
                || ((delay > TimeSpan.Zero || _time.GetElapsedTime(_renewedAt) >= _options.LeaseTtl / 3) && !await RenewAsync(pass.Data, pass.Lease, pass.LeaseMs).ConfigureAwait(false))
                || !await WaitWhilePausedAsync(pass, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    // Holds while the rate says pause, renewing the lease and watching the state; false when the unit must stop (the state moved on or the lease is no longer this runner's).
    private async Task<bool> WaitWhilePausedAsync(PassKeys pass, CancellationToken cancellationToken)
    {
        while (await _rate.PauseReasonAsync(_unit.Id, cancellationToken).ConfigureAwait(false) is not null)
        {
            await Task.Delay(_options.PollInterval, _time, cancellationToken).ConfigureAwait(false);
            if (!await _stillAllowed(cancellationToken).ConfigureAwait(false) || !await RenewAsync(pass.Data, pass.Lease, pass.LeaseMs).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private bool RenewalDue(int sinceRenewal) => sinceRenewal >= _options.RenewEveryKeys || _time.GetElapsedTime(_renewedAt) >= _options.LeaseTtl / 3;

    // Stores the cursor after the batch, so a crash resumes from it; the batch's keys are idempotent, so the rescan of a half-moved one costs time only.
    // Returns the cursor of the next batch, or null with how long to wait when the pass finished or the lease was lost.
    private async Task<(ScanCursor? Cursor, TimeSpan? Wait)> EndBatchAsync(PassKeys pass, ScanCursor next, int patternCount, string scanned, Tally tally)
    {
        next = next with { Failed = next.Failed + tally.Failed };
        var finished = next.Pattern >= patternCount;

        // The counters are written before the pass is recorded, so a finished pass is never seen with its counts missing.
        await FlushAsync(pass.Data, tally).ConfigureAwait(false);
        var step = await pass.Data.Scripts.UnitStepAsync(
            pass.Lease,
            pass.Cursor,
            pass.Pass,
            _token,
            pass.LeaseMs,
            finished ? string.Empty : next.Text,

            // A pass in which a key failed is not a pass: no record, and the next pass starts from zero.
            finished && next.Failed == 0 ? PassRecord.Text(_identity, _moveId, _time.GetUtcNow().ToUnixTimeMilliseconds(), 0, scanned) : string.Empty,
            (long)_options.CursorLife.TotalMilliseconds,
            (long)_options.PassRecordLife.TotalMilliseconds).ConfigureAwait(false);
        if (step == MoveScripts.UnitLeaseLost)
        {
            return (null, null);
        }

        if (finished)
        {
            // The unit is free for another runner while this one waits for the next pass.
            await pass.Data.Scripts.LeaseReleaseAsync(pass.Lease, _token).ConfigureAwait(false);
            return (null, _options.PassPause);
        }

        return (next, null);
    }

    private bool HoldsLease() => _time.GetElapsedTime(_renewedAt) < _options.LeaseTtl;

    private bool ForwardS3() => _plane.RulesFor(_group).Acting is { State: "S3", Direction: MoverPasses.Forward } acting && acting.Id == _identity;

    // A lease that has to be taken again, not extended, lapsed under this runner, so another may have run the unit since.
    private async Task<bool> RenewAsync(KeyMoveData data, RedisKey lease, long leaseMs)
    {
        var sent = _time.GetTimestamp();
        if (await data.Scripts.LeaseAsync(lease, _token, leaseMs).ConfigureAwait(false) != MoveScripts.LeaseRenewed)
        {
            return false;
        }

        _renewedAt = sent;
        return true;
    }

    // One SCAN call, bound to the server that issues the cursor: a cursor of another server, a restarted one or another pattern list starts the pass from zero.
    private async Task<(ScanCursor Next, List<string> Keys, string Source)?> ScanAsync(KeyMoveData data, ScanCursor at, IReadOnlyList<string> patterns, CancellationToken cancellationToken)
    {
        var server = at.Source.Length > 0 && SameEndpoint(at.Source, _unit.Primary) ? _unit.Primary : _unit.ScanServer;
        if (await SourceOfAsync(server).ConfigureAwait(false) is not { } source)
        {
            return null;
        }

        // A stored position is resumed only under the verified server that issued it; one with no server named is no position to resume.
        if ((at.Source.Length > 0 && at.Source != source) || (at.Source.Length == 0 && !at.IsStart))
        {
            at = ScanCursor.Start(_identity, source, at.Patterns);
        }

        at = at with { Source = source };
        if (at.Pattern >= patterns.Count)
        {
            return (at, [], source);
        }

        RedisResult reply;
        try
        {
            reply = await ExecuteScanAsync(server, data, at, patterns, cancellationToken).ConfigureAwait(false);
        }
        catch (RedisException) when (!ReferenceEquals(server, _unit.Primary))
        {
            // A replica cursor is never sent to the primary: the pass starts again from zero there and stays there.
            server = _unit.Primary;
            if (await SourceOfAsync(server).ConfigureAwait(false) is not { } primarySource)
            {
                return null;
            }

            source = primarySource;
            at = ScanCursor.Start(_identity, source, at.Patterns);
            reply = await ExecuteScanAsync(server, data, at, patterns, cancellationToken).ConfigureAwait(false);
        }

        var (cursor, keys) = ReadScanReply(reply);
        return (cursor == "0" ? at with { Pattern = at.Pattern + 1, Cursor = "0" } : at with { Cursor = cursor }, keys, source);
    }

    // The next cursor and the keys of one SCAN reply, with the connector prefix taken off; a key without the prefix is not the mover's.
    private (string Cursor, List<string> Keys) ReadScanReply(RedisResult reply)
    {
        var parts = ((RedisResult[]?)reply)!;
        var keys = new List<string>();
        foreach (var raw in ((RedisResult[]?)parts[1])!)
        {
            if ((string?)raw is { } text && text.StartsWith(_connectorPrefix, StringComparison.Ordinal))
            {
                keys.Add(text[_connectorPrefix.Length..]);
            }
        }

        return (((string?)parts[0])!, keys);
    }

    private Task<RedisResult> ExecuteScanAsync(IServer server, KeyMoveData data, ScanCursor at, IReadOnlyList<string> patterns, CancellationToken cancellationToken) =>
        server.ExecuteAsync(data.Db.Database, "SCAN", [at.Cursor, "MATCH", MasterScanner.EscapeGlob(_connectorPrefix) + patterns[at.Pattern], "COUNT", _options.ScanCount, "TYPE", "string"]).WaitAsync(cancellationToken);

    // Check ownership and key mapping before promotion. EndBatch ends the batch, RetryOom asks for one more try of the key, and anything else is the bytes published.
    private async Task<long> MoveKeyAsync(string key, Tally tally, IMoveOwnership ownership, bool retryOnOom, CancellationToken cancellationToken)
    {
        var owners = _space.OwnersOf(_group, MoveSide.Legacy, key);
        if (owners.Count == 0)
        {
            // Another group's key, an unmanaged cache's, a direct cache's, or none: not the mover's, and not counted.
            return 0;
        }

        if (owners.Count > 1)
        {
            tally.Skip(string.Join('+', owners), "ambiguous");
            if (!_alertedAmbiguity)
            {
                _alertedAmbiguity = true;
                _plane.Alert(_group, "ambiguous_ownership", $"{string.Join(" and ", owners)} own '{key}', which the mover leaves alone");
            }

            return 0;
        }

        var cacheId = owners[0];
        if (!_plane.RulesFor(_group).IsManaged(cacheId))
        {
            return 0;
        }

        if (!_targets.TryGetValue(cacheId, out var target))
        {
            _alerts.Raise(new KeyMoveAlert(_group, "mover_no_target", cacheId));
            return 0;
        }

        if (!_space.IsMovable(_group, MoveSide.Legacy, cacheId, key, null))
        {
            tally.Skip(cacheId, Unmappable);
            return 0;
        }

        // The promotion works on the caller's key, which the cache's own map reads from the in-place key.
        if (_space.CallerKeyOf(_group, MoveSide.Legacy, cacheId, key) is not { } caller)
        {
            tally.Skip(cacheId, Unmappable);
            return 0;
        }

        var started = _time.GetTimestamp();
        var result = await target.MoveAsync(caller, _space.MoveMinTtl(_group, cacheId), _identity, ownership, cancellationToken).ConfigureAwait(false);
        _rate.Observed(_unit.Id, result, _time.GetElapsedTime(started));
        if (retryOnOom && string.Equals(result.SkipReason, "oom", StringComparison.Ordinal))
        {
            return RetryOom;
        }

        if (result.Moved)
        {
            tally.Moved(cacheId);
        }
        else if (result.SkipReason == MoveResult.OwnershipLost)
        {
            // Not this runner's key any more: nothing is counted, and the batch ends so the next owner takes it.
            return EndBatch;
        }
        else if (result.SkipReason is { } reason)
        {
            tally.Skip(cacheId, reason);

            // A unit admitted under another identity means the state moved on: nothing more of this batch is promoted.
            if (reason == "identity")
            {
                return EndBatch;
            }
        }

        return result.Bytes;
    }

    private async Task FlushAsync(KeyMoveData data, Tally tally)
    {
        foreach (var (field, count) in tally.Counts)
        {
            await AddProgressAsync(data, field, count).ConfigureAwait(false);
        }
    }

    private async Task AddProgressAsync(KeyMoveData data, string field, long count)
    {
        try
        {
            await data.Db.HashIncrementAsync(data.Names.Progress(_group), field, count, Master).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // The counters of the hash are a report, not a record the move depends on.
            _ = ex;
        }
    }

    // The lease as one key's promotion sees it: valid locally and in the state it started under, and renewed when a third of its life is spent.
    private sealed class PassOwnership(MoverUnitRunner runner, KeyMoveData data, RedisKey lease, long leaseMs) : IMoveOwnership
    {
        private bool _recorded;

        public bool StillHeld => runner.HoldsLease() && runner.ForwardS3();

        public async ValueTask<bool> HoldsAsync(CancellationToken cancellationToken)
        {
            if (!runner.HoldsLease() || !runner.ForwardS3())
            {
                return false;
            }

            if (runner._time.GetElapsedTime(runner._renewedAt) < runner._options.LeaseTtl / 3)
            {
                return true;
            }

            // Each await can outlast the lease, so what it answered is judged again before it is believed.
            if (!await runner.RenewAsync(data, lease, leaseMs).ConfigureAwait(false) || !runner.HoldsLease() || !runner.ForwardS3())
            {
                return false;
            }

            return await runner._stillAllowed(cancellationToken).ConfigureAwait(false) && runner.HoldsLease() && runner.ForwardS3();
        }

        /// <summary>A new key begins: its publish may be recorded again.</summary>
        public void NewKey() => _recorded = false;

        public void Published(long bytes)
        {
            if (!_recorded)
            {
                _recorded = true;
                runner._rate.Owe(runner._unit.Id, bytes);
            }
        }
    }

    private sealed class Tally(string group)
    {
        public Dictionary<string, long> Counts { get; } = [];

        /// <summary>Keys the pass could not settle: any skip outside the accepted classes.</summary>
        public int Failed { get; private set; }

        public void Moved(string cacheId)
        {
            RouterTelemetry.CountMoverMoved(cacheId, group);
            Add("moved");
        }

        public void Skip(string cacheId, string reason)
        {
            RouterTelemetry.CountMoverSkipped(cacheId, group, reason);
            Add("skipped:" + reason);
            if (!Accepted.Contains(reason))
            {
                Failed++;
            }
        }

        private void Add(string field) => Counts[field] = Counts.GetValueOrDefault(field) + 1;
    }

    // The keys of a batch moved since the lease was last renewed.
    private sealed class RenewalCount
    {
        public int Keys { get; set; }
    }

    // The keys one pass of the unit writes, on the connection it runs on.
    private sealed record PassKeys(KeyMoveData Data, RedisKey Lease, RedisKey Cursor, RedisKey Pass, long LeaseMs);
}
