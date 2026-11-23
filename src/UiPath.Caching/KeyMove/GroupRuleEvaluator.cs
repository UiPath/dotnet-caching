namespace UiPath.Caching.KeyMove;

internal static class GroupRuleEvaluator
{
    private const string Closed = "S5";
    private const string Off = "S0";

    public static bool IsRelevant(GroupView view) => view.Answer == StateAnswer.Unknown
        ? view.Registration.Flagged || view.Registration.Listed || view.RecordFound
        : view.Registration.Listed || view.Answer == StateAnswer.Present || view.RecordFound || view.EverPresent;

    /// <summary>S1 to S4, a listed S0 and any state not yet durable are polled often, S5 and the rest slowly, by the state in force.</summary>
    public static TimeSpan PollInterval(GroupView view, KeyMoveStateOptions options)
    {
        if (view.State is { Durable: false })
        {
            return options.PollInterval;
        }

        return (view.LastKnown?.State ?? view.State?.State) switch
        {
            Closed => options.SlowPollInterval,
            Off when !view.Registration.Listed => options.SlowPollInterval,
            _ => options.PollInterval,
        };
    }

    public static GroupRules Evaluate(GroupView view, KeyMoveStateOptions options)
    {
        var rules = EvaluateRules(view, options);
        if (!rules.Relevant || view.State is not { } state)
        {
            return rules;
        }

        // A cache the state does not manage uses the router's per-group path; a registration that differs from the stored set is reported either way.
        var managed = state.CacheIds.ToHashSet(StringComparer.Ordinal);
        var drift = view.Registration.CacheIds is { } registered && !registered.ToHashSet(StringComparer.Ordinal).SetEquals(managed);
        return rules with { Managed = managed, Alerts = drift ? [.. rules.Alerts, "cache_set_drift"] : rules.Alerts };
    }

    private static GroupRules EvaluateRules(GroupView view, KeyMoveStateOptions options)
    {
        if (!IsRelevant(view))
        {
            return GroupRules.Unaffected;
        }

        var staleAfter = options.StaleAfter(PollInterval(view, options));
        if (view.Answer == StateAnswer.Unknown)
        {
            return Unknown(view, staleAfter, options);
        }

        if (view.SinceRead > staleAfter)
        {
            return Stale(view);
        }

        if (view.Conflict is { } conflict)
        {
            return new GroupRules(true, ReadRule.UnionNativeFirst, WriteRule.FailClosed, view.LastKnown, null, false, true, [conflict]);
        }

        if (view.Answer == StateAnswer.Absent)
        {
            var keeps = view.LastKnown?.State is Off or Closed;
            return new GroupRules(true, keeps ? ReadRule.Follow : ReadRule.UnionNativeFirst, keeps ? WriteRule.Follow : WriteRule.FailClosed, view.LastKnown, null, false, true, ["state_missing"]);
        }

        if (view.State is { Durable: false })
        {
            var left = options.TransitionWait - (view.DurZeroFor ?? TimeSpan.Zero);
            return left > TimeSpan.Zero
                ? new GroupRules(true, ReadRule.UnionNativeFirst, WriteRule.WaitThenFailClosed, view.LastKnown, left, false, true, [])
                : new GroupRules(true, ReadRule.UnionNativeFirst, WriteRule.FailClosed, view.LastKnown, null, false, true, []);
        }

        return new GroupRules(true, ReadRule.Follow, WriteRule.Follow, view.LastKnown, null, false, true, []);
    }

    private static GroupRules Unknown(GroupView view, TimeSpan staleAfter, KeyMoveStateOptions options)
    {
        var ready = view.SinceStart < options.StartupWait;
        var alerts = view.SinceStart > staleAfter ? new[] { "no_snapshot", "node_blind" } : ["no_snapshot"];

        // A closed group keeps serving: its terminal record counts as an answer, and S5 writers delete the source copy until the sweep is known to be done.
        if (view.LastKnown is null && view.Sample is { State: Closed, Durable: true } sample)
        {
            return new GroupRules(true, ReadRule.UnionNativeFirst, WriteRule.Follow, new ActedState(sample.State, sample.Direction, sample.Ep, sample.Id, 1, false), null, false, ready, []);
        }

        return new GroupRules(true, ReadRule.UnionNativeFirst, WriteRule.FailClosed, view.LastKnown, null, false, ready, alerts);
    }

    private static GroupRules Stale(GroupView view)
    {
        var stage = view.LastKnown?.State ?? view.State?.State;
        if (view.Registration.Listed && stage != Closed)
        {
            return new GroupRules(true, stage == Off ? ReadRule.Follow : ReadRule.UnionNativeFirst, WriteRule.FailClosed, view.LastKnown, null, true, true, ["snapshot_stale"]);
        }

        return new GroupRules(true, ReadRule.Follow, WriteRule.Follow, view.LastKnown, null, false, true, ["snapshot_stale"]);
    }
}
