namespace UiPath.Caching.PhaseB;

internal static partial class StepPlanner
{
    // The cache set the nodes registered against the stored one (2.5). In S1 and S2, once every fresh advert shows the same set, X12 brings it in under a new identity that every node acknowledges
    // before X3; from S3 on the set is frozen, and a cache outside it stays unmanaged on every node. Anywhere else the drift is only reported.
    private static (StepPlan? Plan, IReadOnlyList<StageAlert> Alerts) CacheDrift(StepInput i)
    {
        var state = i.State;
        if (state.State == "S0" || i.Fresh.Count == 0)
        {
            return (null, []);
        }

        var expected = state.CacheIds.Order(StringComparer.Ordinal).ToArray();
        var drifting = i.Fresh.Where(a => a.CachesOf(i.Group) is not { } ids || !ids.SequenceEqual(expected, StringComparer.Ordinal)).Select(a => a.Node).ToArray();
        if (drifting.Length == 0)
        {
            return (null, []);
        }

        var alert = new StageAlert("cache_set_drift", string.Join(',', drifting));
        var sets = i.Fresh.Select(a => a.CachesOf(i.Group)).ToArray();
        var agreed = sets[0];
        var rebuildable = state is { State: "S1" or "S2", Direction: "fwd" }
            && agreed is { Count: > 0 }
            && sets.All(s => s is not null && s.SequenceEqual(agreed, StringComparer.Ordinal))
            && agreed.All(IsValidCacheName)
            && i.Flag.Lowered is not <= 2;
        return rebuildable
            ? (new StepPlan(StepKind.RebuildCaches, Caches: StateRecord.EncodeCaches(agreed!)), [alert])
            : (null, [alert]);
    }

    private static bool IsValidCacheName(string name)
    {
        try
        {
            MoveKeyNames.ValidateName(name, nameof(name));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
