namespace UiPath.Caching.PhaseB;

/// <summary>The CLOSED gates C1 to C8 of 6.5, and the forced overrides of C2 and C3 only.</summary>
internal static class CloseGates
{
    /// <summary>C1 to C6 and C8 from memory; C7 is a master census and runs only when these hold. C6 is attested by the stored close consent.</summary>
    public static async Task<CloseGateResult> CheapAsync(StepInput i, StageDriverOptions options)
    {
        var state = i.State;
        var failures = new List<GateFailure>();
        var overrides = new List<string>();
        var forced = Approval.ValidFor(state, ApprovalKinds.CloseForce) is not null;
        if (i.Fresh.Count == 0 || i.Fresh.Any(a => !a.Acknowledges(i.Group, state)))
        {
            failures.Add(new GateFailure("C1", string.Join(',', i.Fresh.Where(a => !a.Acknowledges(i.Group, state)).Select(a => a.Node))));
        }

        var drainedAt = long.TryParse(state.Field(StateFields.DrainedAt), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;
        if (drainedAt is null || i.Now - drainedAt < options.DrainedSoak)
        {
            Overridable("C2", drainedAt is null ? "drained_at_unknown" : "soak", forced, failures, overrides);
        }

        var drain = await i.Drain().ConfigureAwait(false);
        if (drain is null || drain.Canary > 0)
        {
            Overridable("C3", "canary", forced, failures, overrides);
        }

        failures.AddRange(Acknowledged(i, state));
        if (i.Fresh.Where(a => a.Bypasses || !a.HasPb).Select(a => a.Node).ToArray() is { Length: > 0 } bad)
        {
            failures.Add(new GateFailure("C5", string.Join(',', bad)));
        }

        if (i.Flag.Confirmed < options.CloseConfirm)
        {
            failures.Add(new GateFailure("C8", "views_not_confirmed"));
        }

        return new CloseGateResult(failures, overrides);
    }

    /// <summary>C7: a master census taken now shows no more residue than the acknowledged counts.</summary>
    public static async Task<GateFailure?> CensusAsync(StepInput i)
    {
        var census = await i.ResidueCensus().ConfigureAwait(false);
        var grown = i.State.CacheIds.Where(c => census.GetValueOrDefault(c) > (i.State.Residue is { } r && r.TryGetValue(c, out var n) ? n : 0)).ToArray();
        return grown.Length == 0 ? null : new GateFailure("C7", string.Join(',', grown));
    }

    // C4: the stored consent covers the residue the state holds, which is known for every cache, and a NoOrigin cache with residue has no way through but a refill.
    private static IEnumerable<GateFailure> Acknowledged(StepInput i, StateRecord state)
    {
        if (Approval.ValidFor(state, ApprovalKinds.Close) is null && Approval.ValidFor(state, ApprovalKinds.CloseForce) is null)
        {
            yield return new GateFailure("C4", "no_consent");
            yield break;
        }

        var unknown = state.CacheIds.Where(c => state.Residue is null || !state.Residue.ContainsKey(c)).ToArray();
        if (unknown.Length > 0)
        {
            yield return new GateFailure("C4", $"residue_unknown:{string.Join(',', unknown)}");
        }

        foreach (var cache in state.CacheIds)
        {
            if (state.Residue is { } residue && residue.GetValueOrDefault(cache) > 0 && i.Facts.HasNoOrigin(i.Group, [cache]) && !i.Facts.AllAcceptRefill(i.Group, [cache]))
            {
                yield return new GateFailure("C4", $"noorigin_residue:{cache}");
            }
        }
    }

    private static void Overridable(string gate, string reason, bool forced, List<GateFailure> failures, List<string> overrides)
    {
        if (forced)
        {
            overrides.Add(gate);
        }
        else
        {
            failures.Add(new GateFailure(gate, reason));
        }
    }
}
