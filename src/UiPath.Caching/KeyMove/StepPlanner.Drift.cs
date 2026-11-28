namespace UiPath.Caching.KeyMove;

internal static partial class StepPlanner
{
    // Compare registered and stored cache sets. In preparing and switching, once fresh adverts agree, X12 adopts the set under a new identity that every node acknowledges
    // before X3. From copying onward, the set is frozen and outside caches stay unmanaged on every node. Elsewhere, only report drift.
    private static (StepPlan? Plan, IReadOnlyList<StageAlert> Alerts) CacheDrift(StepInput i)
    {
        var state = i.State;
        if (state.State == KeyMoveStates.Off || i.Fresh.Count == 0)
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
        var rebuildable = state is { State: KeyMoveStates.Preparing or KeyMoveStates.Switching, Direction: "fwd" }
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
