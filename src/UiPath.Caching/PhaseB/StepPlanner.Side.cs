namespace UiPath.Caching.PhaseB;

internal static partial class StepPlanner
{
    private static async Task<StepSide> SideEffectsAsync(StepInput i, StageDriverOptions options)
    {
        var state = i.State;
        var alerts = new List<StageAlert>();
        var fields = new List<(string, string)>();
        string? set = null;
        var clear = false;
        if (state.State == "S0" && state.HoldAuto.Length > 0)
        {
            // Nodes are not judged in S0, so a hold left by the group's last run (X6, X10, an abandon) would block a raise until Resume.
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

        if (state is { State: "S4", Direction: "fwd" })
        {
            await RepairForwardAsync(i, fields).ConfigureAwait(false);
        }

        if (state is { State: "S3" or "S4", Direction: "fwd" } && await i.Drain().ConfigureAwait(false) is { Canary: > 0 })
        {
            // The soak of C3 runs from the last pass that counted a key written after S3.
            fields.Add((StateFields.CanaryAt, Number(i.Now.ToUnixTimeMilliseconds())));
        }

        if (state is { State: "S4", Direction: "rev" })
        {
            await RepairReverseAsync(i, fields).ConfigureAwait(false);
        }

        var hold = state.HoldOp.Length > 0 ? $"op:{state.HoldOp}" : state.HoldAuto.Length > 0 && !clear ? $"auto:{state.HoldAuto}" : set is null ? null : $"auto:{set}";
        if (hold is not null)
        {
            alerts.Add(new StageAlert("held", hold));
        }

        return new StepSide(set, clear, AllAcknowledge(i) && state.AllAck != state.Id && state.State != "S0", fields, alerts, hold);
    }

    // A node without pb or without pbg, or a canary over its threshold in the forward direction, holds the group until the cause is gone.
    private static async Task<string?> AutoCauseAsync(StepInput i, StageDriverOptions options)
    {
        if (i.Fresh.Where(a => !a.HasPb).Select(a => a.Node).ToArray() is { Length: > 0 } noPb)
        {
            return "no_pb:" + string.Join(',', noPb);
        }

        if (i.Fresh.Where(a => !a.Lists(i.Group)).Select(a => a.Node).ToArray() is { Length: > 0 } noPbg)
        {
            return "no_pbg:" + string.Join(',', noPbg);
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

    // The residue and the drained time of a forward S4 are written after X4; when a crash or a failed write left them out, the leader writes them from a master census.
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
            var census = await i.ResidueCensus().ConfigureAwait(false);
            fields.AddRange(missing.Select(c => (StateFields.ResiduePrefix + c, Number(census.GetValueOrDefault(c)))));
        }
    }

    // The residue of a reverse S4 is the census of N under this identity. A complete report is written with the identity marker (a changed count voids the consent); a missing or incomplete one is no evidence and writes nothing.
    private static async Task RepairReverseAsync(StepInput i, List<(string, string)> fields)
    {
        var state = i.State;
        if (await i.Drain().ConfigureAwait(false) is not { } census || state.CacheIds.Any(c => !census.Residue.ContainsKey(c)))
        {
            return;
        }

        fields.AddRange(ResidueFields(state, census.Residue).Set.Where(f => state.Residue is not { } held || !held.TryGetValue(f.Field[StateFields.ResiduePrefix.Length..], out var count) || Number(count) != f.Value));
        if (state.Field(StateFields.ResidueId) != state.Id)
        {
            fields.Add((StateFields.ResidueId, state.Id));
        }
    }
}
