namespace UiPath.Caching.PhaseB;

/// <summary>The table of what a node does for a group (2.5), as a pure function of a <see cref="GroupView"/>.</summary>
internal static class GroupRuleEvaluator
{
    private const string Closed = "S5";
    private const string Off = "S0";

    public static bool IsRelevant(GroupView view) => view.Answer == StateAnswer.Unknown
        ? view.Registration.Flagged || view.Registration.Listed || view.RecordFound
        : view.Registration.Listed || view.Answer == StateAnswer.Present || view.RecordFound || view.EverPresent;

    /// <summary>S1 to S4 and a listed group in S0 are polled often, S5 and the rest slowly.</summary>
    public static TimeSpan PollInterval(GroupView view, PhaseBStateOptions options) => view.State?.State switch
    {
        Closed => options.SlowPollInterval,
        Off => view.Registration.Listed ? options.PollInterval : options.SlowPollInterval,
        _ => options.PollInterval,
    };

    public static GroupRules Evaluate(GroupView view, PhaseBStateOptions options)
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

    private static GroupRules Unknown(GroupView view, TimeSpan staleAfter, PhaseBStateOptions options)
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
