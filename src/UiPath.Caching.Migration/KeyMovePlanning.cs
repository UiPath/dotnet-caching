using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The mapping from the rules of a group to the path a cache takes, shared by the string and the hash key move clients.</summary>
internal static class KeyMovePlanning
{
    /// <summary>The path of one cache for a read or a write under the current rules of its group.</summary>
    public static KeyMovePlan PlanFor(IKeyMovePlane plane, string group, string cacheId, bool write)
    {
        var rules = plane.RulesFor(group);
        if (!rules.Relevant || !rules.IsManaged(cacheId))
        {
            return new KeyMovePlan(KeyMoveMode.Plain, rules);
        }

        return new KeyMovePlan(write ? WriteMode(rules) : ReadMode(plane, group, cacheId, rules), rules);
    }

    /// <summary>The mode of a stage name, or <paramref name="otherwise"/> for any other name.</summary>
    public static KeyMoveMode ModeOf(string state, KeyMoveMode otherwise) => state switch
    {
        KeyMoveStates.Ready => KeyMoveMode.Ready,
        KeyMoveStates.Barrier => KeyMoveMode.Barrier,
        KeyMoveStates.Moving => KeyMoveMode.Moving,
        KeyMoveStates.Drained => KeyMoveMode.Drained,
        KeyMoveStates.Closed => KeyMoveMode.Closed,
        _ => otherwise,
    };

    /// <summary>True for a read of moving(rev) under the rules that follow the state: an N hit is promoted to L.</summary>
    public static bool PromotesReverse(KeyMovePlan plan) =>
        plan.Mode == KeyMoveMode.Union && plan.Rules.Reads == ReadRule.Follow && plan.Act is { State: KeyMoveStates.Moving, Direction: "rev" };

    /// <summary>True for the write modes of the reverse direction, which only the clients that run their forms leave open.</summary>
    public static bool IsReverseWrite(KeyMoveMode mode) => mode is KeyMoveMode.ReverseS1 or KeyMoveMode.ReverseS3 or KeyMoveMode.ReverseS4;

    private static KeyMoveMode ReverseModeOf(string state) => state switch
    {
        KeyMoveStates.Ready => KeyMoveMode.ReverseS1,
        KeyMoveStates.Moving => KeyMoveMode.ReverseS3,
        KeyMoveStates.Drained => KeyMoveMode.ReverseS4,
        _ => KeyMoveMode.Unavailable,
    };

    private static KeyMoveMode WriteMode(GroupRules rules)
    {
        if (rules.Writes != WriteRule.Follow)
        {
            return rules.Writes == WriteRule.WaitThenFailClosed ? KeyMoveMode.Wait : KeyMoveMode.Unavailable;
        }

        return rules.Acting switch
        {
            null or { State: KeyMoveStates.NotStarted } => KeyMoveMode.Plain,
            { Direction: "rev", State: var state } => ReverseModeOf(state),
            { State: var state } => ModeOf(state, KeyMoveMode.Unavailable),
        };
    }

    private static KeyMoveMode ReadMode(IKeyMovePlane plane, string group, string cacheId, GroupRules rules)
    {
        if (rules.Reads == ReadRule.UnionNativeFirst)
        {
            return KeyMoveMode.Union;
        }

        if (rules.Acting is null or { State: KeyMoveStates.NotStarted })
        {
            return KeyMoveMode.Plain;
        }

        var act = rules.Acting;
        if (act.Direction == "rev")
        {
            return KeyMoveMode.Union;
        }

        var mode = ModeOf(act.State, KeyMoveMode.Union);
        if (mode is KeyMoveMode.Ready or KeyMoveMode.Barrier && act.NPop != 0)
        {
            return KeyMoveMode.Union;
        }

        if (mode == KeyMoveMode.Drained)
        {
            return plane.KeepsSourceFallback(group, cacheId) ? KeyMoveMode.Union : KeyMoveMode.Closed;
        }

        return mode;
    }
}
