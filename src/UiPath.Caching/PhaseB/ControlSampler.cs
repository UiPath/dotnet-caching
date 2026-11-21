using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>Reads control records on the masters: the one probe slot of a group, or a sample of four slots per master.</summary>
/// <param name="controlDatabase">The view that names control records (with the connector prefix).</param>
internal sealed class ControlSampler(IDatabase controlDatabase, MoveKeyNames names, IStateTopology topology)
{
    private const int SlotsPerMaster = 4;
    private const int Attempts = 3;
    private const CommandFlags Master = CommandFlags.DemandMaster;

    /// <summary>The record of the first slot of the slot set; null when the group has none there.</summary>
    public async Task<ControlSample?> ProbeAsync(string group, CancellationToken cancellationToken) =>
        ControlSample.Parse(await controlDatabase.HashGetAsync(names.Control(group, names.SlotSet()[0]), ControlSample.Fields, Master).WaitAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>The records with the highest <c>ep</c> among four slots of the slot set owned by each master, read again when the slot map changed during the read.</summary>
    public async Task<ControlSample?> SampleAsync(string group, CancellationToken cancellationToken)
    {
        var set = names.SlotSet();
        for (var attempt = 0; ; attempt++)
        {
            var before = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
            var sampled = before.Masters.SelectMany(m => Pick(set, m)).Distinct().ToArray();
            var reads = sampled.Select(slot => controlDatabase.HashGetAsync(names.Control(group, slot), ControlSample.Fields, Master)).ToArray();
            await Task.WhenAll(reads).WaitAsync(cancellationToken).ConfigureAwait(false);
            var after = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
            if (after.MovedSince(before, sampled).Count == 0 || attempt + 1 == Attempts)
            {
                return Aggregate([.. sampled.Zip(reads, (slot, r) => ControlSample.Parse(r.Result) is { } found ? found with { Slot = slot } : null).OfType<ControlSample>()]);
            }
        }
    }

    // Every record at the highest epoch counts: another id, state or direction there is a conflict, and one committed record is enough for the epoch to count as committed.
    private static ControlSample? Aggregate(ControlSample[] found)
    {
        if (found.Length == 0)
        {
            return null;
        }

        var top = found.Max(s => s.Ep);
        var atTop = found.Where(s => s.Ep == top).ToArray();
        var first = atTop[0];
        return first with
        {
            Durable = atTop.Any(s => s.Durable),
            Conflicting = atTop.Any(s => s.Id != first.Id || s.State != first.State || s.Direction != first.Direction),
        };
    }

    private static IEnumerable<int> Pick(IReadOnlyList<int> set, MasterLayout master)
    {
        var owned = set.Where(slot => master.Slots.Any(r => slot >= r.From && slot <= r.To)).ToArray();
        return owned.Length <= SlotsPerMaster ? owned : Enumerable.Range(0, SlotsPerMaster).Select(i => owned[i * owned.Length / SlotsPerMaster]);
    }
}
