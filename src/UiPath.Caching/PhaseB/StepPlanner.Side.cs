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
        if (state.State is "S1" or "S2" or "S3" or "S4")
        {
            var cause = await AutoCauseAsync(i, options).ConfigureAwait(false);
            if (cause is not null && state.HoldAuto.Length == 0)
            {
                set = cause;
            }
            else if (cause is null && state.HoldAuto.Length > 0)
            {
                clear = true;
            }
        }

        if (state is { State: "S4", Direction: "fwd" } && state.Field(StateFields.DrainedAt).Length == 0)
        {
            fields.Add((StateFields.DrainedAt, Number(i.Now.ToUnixTimeMilliseconds())));
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
}
