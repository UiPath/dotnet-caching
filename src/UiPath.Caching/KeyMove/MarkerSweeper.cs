using System.Collections.Concurrent;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>
/// The leader's removal of markers that no writer will remove: an obsolete one goes by SMGC, and a current one whose writer is gone (it has stood for longer than a write can run) is helped,
/// by the deletion of the superseded copy fenced by the marker's binding and then the removal of the exact marker.
/// </summary>
internal sealed class MarkerSweeper(IDatabase control, MoveScripts scripts, MoveKeyNames names, KeyMoveStateOptions options, TimeProvider time)
{
    private const int Batch = 512;
    private const int ScanPage = 500;
    private const CommandFlags Master = CommandFlags.DemandMaster;
    private const string FieldPrefix = "sm:";

    private readonly ConcurrentDictionary<string, long> _firstSeen = new(StringComparer.Ordinal);

    private enum Help
    {
        Stop,
        Deleted,
        Absent,
    }

    public static bool Applies(StateRecord state) => ControlUpkeep.Applies(state) && state.Direction == "fwd";

    /// <summary>One pass over the group's records, found by the length of each: a record that holds no more than its fixed fields and gates has no marker.</summary>
    public async Task<MarkerSweepResult> SweepAsync(string group, StateRecord state, CancellationToken cancellationToken)
    {
        var total = new MarkerSweepResult();
        if (!Applies(state))
        {
            return total;
        }

        // lim and pk are not assumed: a record that has neither is scanned at one field more than the state's own.
        var floor = ControlRecord.FixedFields - 2 + state.CacheIds.Count;
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in names.SlotSet().Chunk(Batch))
        {
            var lengths = await Task.WhenAll(chunk.Select(slot => control.HashLengthAsync(names.Control(group, slot), Master))).WaitAsync(cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < chunk.Length; i++)
            {
                if (lengths[i] > floor)
                {
                    var part = await SweepSlotAsync(group, state, chunk[i], live, cancellationToken).ConfigureAwait(false);
                    total = new MarkerSweepResult(total.Seen + part.Seen, total.Obsolete + part.Obsolete, total.Helped + part.Helped, total.Orphans + part.Orphans);
                }
            }
        }

        KeyMoveTelemetry.CountMarkerOrphans(group, total.Orphans);

        foreach (var key in _firstSeen.Keys.Where(k => k.StartsWith(group + "\n", StringComparison.Ordinal) && !live.Contains(k)))
        {
            _firstSeen.TryRemove(key, out _);
        }

        return total;
    }

    private async Task<MarkerSweepResult> SweepSlotAsync(string group, StateRecord state, int slot, HashSet<string> live, CancellationToken cancellationToken)
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
            live.Add(seen);
            var since = _firstSeen.GetOrAdd(seen, _ => time.GetTimestamp());
            if (time.GetElapsedTime(since) <= options.MarkerOrphanAfter)
            {
                continue;
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
