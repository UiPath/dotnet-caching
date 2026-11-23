using System.Collections.Concurrent;

namespace UiPath.Caching.KeyMove;

/// <summary>Counts the admitted units of each group under their identity; a node acknowledges a new identity only when no unit is counted under an older one.</summary>
internal sealed class EpochGate
{
    private readonly ConcurrentDictionary<string, GroupUnits> _groups = new(StringComparer.Ordinal);

    /// <summary>New units are admitted under this identity from now on; an identity below the one in force is ignored.</summary>
    public void Switch(string group, long ep, string id)
    {
        var units = _groups.GetOrAdd(group, _ => new GroupUnits());
        lock (units)
        {
            if (units.Current is { } current && (ep < current.Ep || (ep == current.Ep && id == current.Id)))
            {
                return;
            }

            units.Current = (ep, id);
        }
    }

    /// <summary>Admits a unit under the identity in force; null while the node has none. The caller applies the rules of the snapshot first, so a unit is never admitted at dur=0.</summary>
    public AdmittedUnit? Enter(string group, bool promotion = false)
    {
        var units = _groups.GetOrAdd(group, _ => new GroupUnits());
        lock (units)
        {
            if (units.Current is not { } current)
            {
                return null;
            }

            units.Add(current, promotion, 1);
            return new AdmittedUnit(this, group, current.Ep, current.Id, promotion);
        }
    }

    public int OlderUnits(string group)
    {
        if (!_groups.TryGetValue(group, out var units))
        {
            return 0;
        }

        lock (units)
        {
            return units.Counts.Where(c => units.Current is not { } current || c.Key != current).Sum(c => c.Value.Total);
        }
    }

    /// <summary>Promotion units in flight, for the <c>pi</c> token.</summary>
    public int PromotionUnits(string group)
    {
        if (!_groups.TryGetValue(group, out var units))
        {
            return 0;
        }

        lock (units)
        {
            return units.Counts.Values.Sum(c => c.Promotions);
        }
    }

    internal bool Move(AdmittedUnit unit, string group, bool promotion)
    {
        var units = _groups[group];
        lock (units)
        {
            if (units.Current is not { } current || (unit.Ep == current.Ep && unit.Id == current.Id))
            {
                return false;
            }

            units.Add((unit.Ep, unit.Id), promotion, -1);
            units.Add(current, promotion, 1);
            unit.Rebind(current.Ep, current.Id);
            return true;
        }
    }

    internal void Leave(string group, long ep, string id, bool promotion)
    {
        var units = _groups[group];
        lock (units)
        {
            units.Add((ep, id), promotion, -1);
        }
    }

    private sealed class GroupUnits
    {
        public (long Ep, string Id)? Current { get; set; }

        public Dictionary<(long Ep, string Id), Count> Counts { get; } = [];

        public void Add((long Ep, string Id) identity, bool promotion, int by)
        {
            var count = Counts.GetValueOrDefault(identity) ?? new Count();
            count.Total += by;
            count.Promotions += promotion ? by : 0;
            if (count.Total == 0)
            {
                Counts.Remove(identity);
            }
            else
            {
                Counts[identity] = count;
            }
        }
    }

    private sealed class Count
    {
        public int Total { get; set; }

        public int Promotions { get; set; }
    }
}
