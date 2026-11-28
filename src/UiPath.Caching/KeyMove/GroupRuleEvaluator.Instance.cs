namespace UiPath.Caching.KeyMove;

// A durable Src record can establish a newer identity during a Dst outage only while the Dst holds no production data.
internal static partial class GroupRuleEvaluator
{
    public static GroupRules EvaluateInstance(GroupView view, KeyMoveStateOptions options) =>
        WithManaged(view, InstanceRules(view, options));

    // A newer identity must be durable on its Src home before a node can adopt it.
    internal static bool SrcFallbackAdopts(ControlSample sample, ActedState? last) =>
        sample is { SrcHome: true, SrcDurable: true, NPop: 0, Conflicting: false } && (last is null || sample.Ep > last.Ep);

    private static GroupRules InstanceRules(GroupView view, KeyMoveStateOptions options)
    {
        if (!IsRelevant(view))
        {
            return GroupRules.Unaffected;
        }

        var staleAfter = options.StaleAfter(PollInterval(view, options));
        var unreachable = view.Answer == StateAnswer.Unknown || view.DstReadFailed || view.SinceRead > staleAfter;
        return unreachable ? WhileDstUnreachable(view, staleAfter, options) : EvaluateRules(view, options);
    }

    private static GroupRules WhileDstUnreachable(GroupView view, TimeSpan staleAfter, KeyMoveStateOptions options)
    {
        var last = view.LastKnown;
        if (view.Sample is { } sample && (last is null || sample.Ep > last.Ep))
        {
            return sample.Conflicting
                ? new GroupRules(true, ReadRule.UnionNativeFirst, WriteRule.FailClosed, last, null, false, true, ["identity_conflict", KeyMoveGateIds.DstUnreachable])
                : NewerIdentity(view, sample, last, options);
        }

        if (last is not null)
        {
            return KeepLastKnown(last);
        }

        // No state was ever read, and Src shows no newer identity. An empty sample means off; no sample means the node is blind.
        return view.SampleKnown
            ? new GroupRules(true, ReadRule.Follow, WriteRule.Follow, null, null, false, true, [KeyMoveGateIds.DstUnreachable])
            : Unknown(view, staleAfter, options);
    }

    // The Src record shows an identity the node has not acted under.
    private static GroupRules NewerIdentity(GroupView view, ControlSample sample, ActedState? last, KeyMoveStateOptions options)
    {
        if (!sample.SrcHome)
        {
            // Only the Dst records hold it so far: the Src is still at the old identity and its scripts still pass.
            return last is null ? new GroupRules(true, ReadRule.Follow, WriteRule.Follow, null, null, false, true, [KeyMoveGateIds.DstUnreachable]) : KeepLastKnown(last);
        }

        if (!sample.SrcDurable)
        {
            var left = options.TransitionWait - (view.SampleDurZeroFor ?? TimeSpan.Zero);
            return left > TimeSpan.Zero
                ? new GroupRules(true, ReadRule.Follow, WriteRule.WaitThenFailClosed, last, left, false, true, [KeyMoveGateIds.DstUnreachable])
                : new GroupRules(true, ReadRule.Follow, WriteRule.FailClosed, last, null, false, true, [KeyMoveGateIds.DstUnreachable]);
        }

        if (SrcFallbackAdopts(sample, last))
        {
            return new GroupRules(true, ReadRule.Follow, WriteRule.Follow, new ActedState(sample.State, sample.Direction, sample.Ep, sample.Id, 0, false), null, false, true, [KeyMoveGateIds.DstUnreachable]);
        }

        // A done group keeps serving, as when the state is unknown.
        if (last is null && sample is { State: Done, Durable: true })
        {
            return new GroupRules(true, ReadRule.UnionNativeFirst, WriteRule.Follow, new ActedState(sample.State, sample.Direction, sample.Ep, sample.Id, 1, false), null, false, true, [KeyMoveGateIds.DstUnreachable]);
        }

        return new GroupRules(true, ReadRule.UnionNativeFirst, WriteRule.FailClosed, last, null, false, true, [KeyMoveGateIds.DstUnreachable]);
    }

    // Nothing newer is known: the last known rules. Only a state that already holds data on the Dst (nPop 1) fails done, and a done one keeps serving.
    private static GroupRules KeepLastKnown(ActedState last) => last.NPop == 0 || last.State == Done
        ? new GroupRules(true, ReadRule.Follow, WriteRule.Follow, last, null, false, true, [KeyMoveGateIds.DstUnreachable])
        : new GroupRules(true, ReadRule.UnionNativeFirst, WriteRule.FailClosed, last, null, true, true, [KeyMoveGateIds.DstUnreachable]);
}
