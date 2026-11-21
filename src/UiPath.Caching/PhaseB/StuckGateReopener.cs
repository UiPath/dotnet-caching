using System.Collections.Concurrent;
using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The leader's repair of a gate left closed by a Clear whose node crashed: a <c>CLOSED</c> gate with no <c>clrlock</c> is reopened, expecting the value it read; one closed for too long raises <c>gate_stuck</c>.</summary>
internal sealed class StuckGateReopener(ControlRecordReader records, MoveScripts controlScripts, IDatabase stateDatabase, MoveKeyNames names, PhaseBStateOptions options, TimeProvider time)
{
    private const int Batch = 512;

    private readonly ConcurrentDictionary<string, long> _closedSince = new(StringComparer.Ordinal);

    /// <returns>The cache ids whose gates were reopened, and the ones stuck past <see cref="PhaseBStateOptions.GateStuckAfter"/>.</returns>
    public async Task<(IReadOnlyList<string> Reopened, IReadOnlyList<string> Stuck)> CheckAsync(string group, StateRecord state, CancellationToken cancellationToken)
    {
        var reopened = new List<string>();
        var stuck = new List<string>();
        if (!ControlUpkeep.Applies(state))
        {
            return (reopened, stuck);
        }

        var fields = state.CacheIds.Select(c => (RedisValue)("c:" + c)).ToArray();
        var held = await records.ReadFieldsAsync(group, fields, cancellationToken).ConfigureAwait(false);
        for (var n = 0; n < state.CacheIds.Count; n++)
        {
            var cid = state.CacheIds[n];
            var closed = held.Select(h => (string?)h.Values[n]).Where(v => v is not null && v.StartsWith("CLOSED:", StringComparison.Ordinal)).Select(v => v!).Distinct(StringComparer.Ordinal).ToArray();
            var key = $"{group}|{cid}";
            if (closed.Length == 0)
            {
                _closedSince.TryRemove(key, out _);
                continue;
            }

            var since = _closedSince.GetOrAdd(key, _ => time.GetTimestamp());
            if (time.GetElapsedTime(since) > options.GateStuckAfter)
            {
                stuck.Add(cid);
            }

            if (await stateDatabase.KeyExistsAsync(names.ClearLock(group, cid), CommandFlags.DemandMaster).ConfigureAwait(false))
            {
                continue;
            }

            foreach (var value in closed)
            {
                await ReopenAsync(group, state.Id, cid, value).ConfigureAwait(false);
            }

            reopened.Add(cid);
        }

        return (reopened, stuck);
    }

    private async Task ReopenAsync(string group, string id, string cid, string closed)
    {
        var open = "OPEN:" + MoveScripts.NewNonce();
        foreach (var chunk in names.SlotSet().Chunk(Batch))
        {
            await Task.WhenAll(chunk.Select(slot => controlScripts.GateAsync(names.Control(group, slot), id, cid, open, closed))).ConfigureAwait(false);
        }
    }
}
