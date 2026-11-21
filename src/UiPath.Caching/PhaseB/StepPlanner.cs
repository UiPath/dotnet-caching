using System.Globalization;

namespace UiPath.Caching.PhaseB;

/// <summary>The leader's step logic of 2.0 and 2.2 as a function of what it read: the next transition, a wait with its alert, and the side effects. It never advances without the flag.</summary>
internal static partial class StepPlanner
{
    public static async Task<StepPlan> DecideAsync(StepInput i, StageDriverOptions options)
    {
        var state = i.State;
        if (i.Conflict == "commit_mismatch")
        {
            return new StepPlan(StepKind.Resume, Alerts: [new StageAlert("commit_mismatch", i.Group)]);
        }

        if (i.Conflict is not null)
        {
            return StepPlan.Wait(i.Conflict, i.Group);
        }

        if (!state.Durable)
        {
            return InFlight(i, options);
        }

        var side = await SideEffectsAsync(i, options).ConfigureAwait(false);
        var plan = state.Field(StateFields.ColdStart).Length > 0 ? new StepPlan(StepKind.ColdStart) : await StageStepAsync(i, options, side).ConfigureAwait(false);
        return plan with
        {
            Alerts = [.. plan.AlertList, .. side.Alerts],
            AutoHoldSet = side.HoldSet,
            AutoHoldClear = side.HoldClear,
            AllAck = side.AllAck,
            Fields = [.. plan.FieldList, .. side.Fields],
        };
    }

    /// <summary>The flag value a state stands for: 2 for S0, 3 for S1 to S3, 4 for S4, 5 for S5.</summary>
    public static int StageFlagOf(StateRecord state) => state.State switch
    {
        "S4" => 4,
        "S5" => 5,
        "S0" => 2,
        _ => 3,
    };

    private static string Outstanding(StepInput i) =>
        string.Join(',', i.Fresh.Where(a => !a.Acknowledges(i.Group, i.State)).Select(a => a.Node));

    private static bool AllAcknowledge(StepInput i) => i.Fresh.Count > 0 && i.Fresh.All(a => a.Acknowledges(i.Group, i.State));

    // The views a forward step needs: every fresh node at or above the stage. Null when they do; otherwise nothing, or what blocks when some node already asks.
    private static StepPlan? FlagBlocks(StepInput i, int needed)
    {
        if (i.Flag.AllAtLeast(needed))
        {
            return null;
        }

        if (!i.Fresh.Any(a => a.FlagOf(i.Group) >= needed))
        {
            return StepPlan.Nothing;
        }

        var nodes = string.Join(',', i.Fresh.Where(a => a.FlagOf(i.Group) is not { } v || v < needed).Select(a => a.Node));
        return StepPlan.Wait("stage_pending", $"flag_view:{nodes}");
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
