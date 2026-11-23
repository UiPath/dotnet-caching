namespace UiPath.Caching.KeyMove;

internal static partial class StepPlanner
{
    private static async Task<StepSide> SideEffectsAsync(StepInput i, StageDriverOptions options)
    {
        var state = i.State;
        var alerts = new List<StageAlert>();
        var fields = new List<(string, string)>();
        var (set, clear) = await PlanAutomaticHoldAsync(i, options, state).ConfigureAwait(false);

        if (state is { State: "S4", Direction: "fwd" })
        {
            await RepairForwardAsync(i, fields).ConfigureAwait(false);
        }

        if (state is { State: "S3" or "S4", Direction: "fwd" } && await i.Drain().ConfigureAwait(false) is { Canary: > 0 })
        {
            // A pass that counts a post-transition write restarts the soak.
            fields.Add((StateFields.CanaryAt, Number(i.Now.ToUnixTimeMilliseconds())));
        }

        if (state is { State: "S4", Direction: "rev" })
        {
            await RepairReverseAsync(i, fields).ConfigureAwait(false);
        }

        return DescribeSideEffects(i, state, set, clear, fields, alerts);
    }

    private static async Task<(string? Set, bool Clear)> PlanAutomaticHoldAsync(StepInput i, StageDriverOptions options, StateRecord state)
    {
        string? set = null;
        var clear = false;
        if (state.State == "S0" && state.HoldAuto.Length > 0)
        {
            // Clear stale automatic holds in S0 because the node checks that would clear them are inactive.
            clear = true;
        }

        if (state.State is "S1" or "S2" or "S3" or "S4")
        {
            var cause = await AutoCauseAsync(i, options).ConfigureAwait(false);
            if (cause is not null && state.HoldAuto.Length == 0)
            {
                set = cause;
            }
            else if (cause is null && state.HoldAuto.Length > 0)
            {
                clear = !state.HoldAuto.StartsWith("canary", StringComparison.Ordinal) || await CleanPassAsync(i).ConfigureAwait(false);
            }
        }

        return (set, clear);
    }

    private static StepSide DescribeSideEffects(StepInput i, StateRecord state, string? set, bool clear, List<(string, string)> fields, List<StageAlert> alerts)
    {
        var hold = HoldReason(state, set, clear);
        if (hold is not null)
        {
            alerts.Add(new StageAlert("held", hold));
        }

        return new StepSide(set, clear, AllAcknowledge(i) && state.AllAck != state.Id && state.State != "S0", fields, alerts, hold);
    }

    private static string? HoldReason(StateRecord state, string? set, bool clear)
    {
        if (state.HoldOp.Length > 0)
        {
            return $"op:{state.HoldOp}";
        }

        if (state.HoldAuto.Length > 0 && !clear)
        {
            return $"auto:{state.HoldAuto}";
        }

        return set is null ? null : $"auto:{set}";
    }

    // A node without the key move tokens, or a canary over its threshold in the forward direction, holds the group until the cause is gone.
    private static async Task<string?> AutoCauseAsync(StepInput i, StageDriverOptions options)
    {
        if (i.Fresh.Where(a => !a.HasKm).Select(a => a.Node).ToArray() is { Length: > 0 } noKm)
        {
            return "no_km:" + string.Join(',', noKm);
        }

        if (i.Fresh.Where(a => !a.Lists(i.Group)).Select(a => a.Node).ToArray() is { Length: > 0 } noKmg)
        {
            return "no_kmg:" + string.Join(',', noKmg);
        }

        if (i.State is { State: "S3" or "S4", Direction: "fwd" } && await i.Drain().ConfigureAwait(false) is { } drain && drain.Canary > options.CanaryThreshold)
        {
            return "canary";
        }

        return null;
    }

    // A hold set by the canary clears only after a pass that counted none; no report is no evidence. The canary only counts in reverse, so it holds nothing there.
    private static async Task<bool> CleanPassAsync(StepInput i) =>
        i.State.Direction == "rev" || await i.Drain().ConfigureAwait(false) is { Canary: 0 };

    // The residue and the drained time of a forward S4 are written after X4; when a crash or a failed write left them out, the leader writes them from a master inventory.
    private static async Task RepairForwardAsync(StepInput i, List<(string, string)> fields)
    {
        var state = i.State;
        if (state.Field(StateFields.DrainedAt).Length == 0)
        {
            fields.Add((StateFields.DrainedAt, Number(i.Now.ToUnixTimeMilliseconds())));
        }

        var missing = state.CacheIds.Where(c => state.Residue is null || !state.Residue.ContainsKey(c)).ToArray();
        if (missing.Length > 0 && state.HoldOp.Length == 0)
        {
            var inventory = await i.ResidueInventory().ConfigureAwait(false);
            fields.AddRange(missing.Select(c => (StateFields.ResiduePrefix + c, Number(inventory.GetValueOrDefault(c)))));
        }
    }

    // The residue of a reverse S4 is the inventory of N under this identity. A complete report is written with the identity marker (a changed count voids the consent); a missing or incomplete one is no evidence and writes nothing.
    private static async Task RepairReverseAsync(StepInput i, List<(string, string)> fields)
    {
        var state = i.State;
        if (await i.Drain().ConfigureAwait(false) is not { } inventory || state.CacheIds.Any(c => !inventory.Residue.ContainsKey(c)))
        {
            return;
        }

        fields.AddRange(ResidueFields(state, inventory.Residue).Set.Where(f => state.Residue is not { } held || !held.TryGetValue(f.Field[StateFields.ResiduePrefix.Length..], out var count) || Number(count) != f.Value));
        if (state.Field(StateFields.ResidueId) != state.Id)
        {
            fields.Add((StateFields.ResidueId, state.Id));
        }
    }
}
