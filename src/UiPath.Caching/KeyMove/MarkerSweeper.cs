using System.Collections.Concurrent;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>The leader's removal of markers that no writer will remove, in two modes (the orphans of a forward run, and the retiring generation in returning): an obsolete one goes by SMGC, and a current one whose writer is gone (it has stood for longer than a write can run) is helped, by the deletion of the superseded copy fenced by the marker's binding and then the removal of the exact marker.</summary>
internal sealed class MarkerSweeper(IDatabase control, MoveScripts scripts, MoveKeyNames names, KeyMoveStateOptions options, TimeProvider time)
{
    private const int Batch = 512;
    private const int ScanPage = 500;
    private const CommandFlags Master = CommandFlags.DemandMaster;
    private const string FieldPrefix = "sm:";

    private readonly ConcurrentDictionary<string, long> _firstSeen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _retireFrom = new(StringComparer.Ordinal);

    private enum Help
    {
        Stop,
        Deleted,
        Absent,
    }

    /// <summary>The field count at or below which a record holds no marker: the fixed fields, the two of the binding, and a gate per cache. A marker comes with <c>pk</c> and a limit written before it.</summary>
    public static long Floor(StateRecord state) =>
        ControlRecord.FixedFields + (state.ForwardGeneration.Length > 0 || state.RetiringGeneration.Length > 0 ? 2 : 0) + state.CacheIds.Count;

    public static bool Applies(StateRecord state, SweepMode mode) => mode == SweepMode.Retire
        ? state is { State: KeyMoveStates.Preparing, Direction: "rev", Durable: true } && state.RetiringGeneration.Length > 0
        : ControlUpkeep.Applies(state) && state.Direction == "fwd";

    /// <summary>One pass over the group's records, found by the length of each: a record that holds no more than its fixed fields and gates has no marker.</summary>
    public async Task<MarkerSweepResult> SweepAsync(string group, StateRecord state, SweepMode mode, CancellationToken cancellationToken)
    {
        var total = new MarkerSweepResult();
        if (!Applies(state, mode))
        {
            return total;
        }

        var floor = Floor(state);
        var live = new HashSet<string>(StringComparer.Ordinal);
        var slots = names.SlotSet();
        var from = mode == SweepMode.Retire && _retireFrom.TryGetValue(group, out var resume) && resume < slots.Count ? resume : 0;
        var started = time.GetTimestamp();
        var stopped = false;
        for (var at = from; at < slots.Count && !stopped; at += Batch)
        {
            total = Add(total, await SweepBatchAsync(group, state, slots.Skip(at).Take(Batch).ToArray(), mode, floor, live, cancellationToken).ConfigureAwait(false));

            // The retire sweep runs inside a step of the leader, so it stops after the discovery budget and the next step goes on from the slot it stopped at.
            if (mode == SweepMode.Retire && at + Batch < slots.Count && time.GetElapsedTime(started) >= options.RetireDiscoveryBudget)
            {
                _retireFrom[group] = at + Batch;
                stopped = true;
            }
        }

        if (mode == SweepMode.Retire && !stopped)
        {
            _retireFrom.TryRemove(group, out _);
        }

        KeyMoveTelemetry.CountMarkerOrphans(group, total.Orphans);

        foreach (var key in _firstSeen.Keys.Where(k => k.StartsWith(group + "\n", StringComparison.Ordinal) && !live.Contains(k)))
        {
            _firstSeen.TryRemove(key, out _);
        }

        return total;
    }

    private static MarkerSweepResult Add(MarkerSweepResult total, MarkerSweepResult part) =>
        new(total.Seen + part.Seen, total.Obsolete + part.Obsolete, total.Helped + part.Helped, total.Orphans + part.Orphans);

    private async Task<MarkerSweepResult> SweepBatchAsync(string group, StateRecord state, int[] chunk, SweepMode mode, long floor, HashSet<string> live, CancellationToken cancellationToken)
    {
        var total = new MarkerSweepResult();
        var lengths = await Task.WhenAll(chunk.Select(slot => control.HashLengthAsync(names.Control(group, slot), Master))).WaitAsync(cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < chunk.Length; i++)
        {
            if (lengths[i] > floor)
            {
                total = Add(total, await SweepSlotAsync(group, state, chunk[i], mode, live, cancellationToken).ConfigureAwait(false));
            }
        }

        return total;
    }

    private async Task<MarkerSweepResult> SweepSlotAsync(string group, StateRecord state, int slot, SweepMode mode, HashSet<string> live, CancellationToken cancellationToken)
    {
        var record = names.Control(group, slot);

        // One unit is pinned to the identity of the state it read: every script it sends is checked against the record, so a unit that is late is refused.
        var head = await control.HashGetAsync(record, [MarkerBinding.IdentityField, MarkerBinding.BindingFieldName, MarkerBinding.RetiringField], Master).ConfigureAwait(false);
        if (head[0] != state.Id)
        {
            return default;
        }

        var current = await control.HashGetAsync(record, head[1].IsNull ? MarkerBinding.IdentityField : head[1].ToString(), Master).ConfigureAwait(false);
        var markers = new List<(string Field, string Value)>();
        await foreach (var entry in control.HashScanAsync(record, FieldPrefix + "*", pageSize: ScanPage, flags: Master).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            markers.Add((entry.Name.ToString(), entry.Value.ToString()));
        }

        var (obsolete, helped, orphans) = (0, 0, 0);
        foreach (var (field, value) in markers)
        {
            if (!MarkerBinding.IsLive(value, current.IsNull ? null : current.ToString(), head[2].IsNull ? null : head[2].ToString()))
            {
                obsolete += await scripts.SupersessionGcAsync(record, field, value).ConfigureAwait(false) == 1 ? 1 : 0;
                continue;
            }

            var seen = $"{group}\n{slot}\n{field}\n{value}";
            if (mode == SweepMode.Orphans)
            {
                live.Add(seen);
                var since = _firstSeen.GetOrAdd(seen, _ => time.GetTimestamp());
                if (time.GetElapsedTime(since) <= options.MarkerOrphanAfter)
                {
                    continue;
                }
            }

            var outcome = await HelpAsync(group, state.Id, record, field, value, cancellationToken).ConfigureAwait(false);
            if (outcome == Help.Stop)
            {
                break;
            }

            helped += outcome == Help.Deleted ? 1 : 0;
            orphans++;
            _firstSeen.TryRemove(seen, out _);
        }

        return new MarkerSweepResult(markers.Count, obsolete, helped, orphans);
    }

    // Present: the copy is deleted under the marker's binding, then the marker goes. Absent: it has been absent since after the marker was read, for good, and the marker goes.
    private async Task<Help> HelpAsync(string group, string id, RedisKey record, string field, string value, CancellationToken cancellationToken)
    {
        if (!field.StartsWith(FieldPrefix + names.ConnectorPrefix, StringComparison.Ordinal))
        {
            return Help.Stop;
        }

        var l = field[(FieldPrefix.Length + names.ConnectorPrefix.Length)..];
        var deleted = false;
        if (await control.KeyExistsAsync(l, Master).ConfigureAwait(false))
        {
            var code = await scripts.SourceDeleteGuardedAsync(l, names.ControlFor(group, l), id, MarkerBinding.BindingOf(value)).ConfigureAwait(false);
            if (code is MoveScripts.IdentityMismatch or MoveScripts.BindingEnded)
            {
                return Help.Stop;
            }

            deleted = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = await scripts.UnmarkAsync(record, field, value).ConfigureAwait(false);
        return deleted ? Help.Deleted : Help.Absent;
    }
}
