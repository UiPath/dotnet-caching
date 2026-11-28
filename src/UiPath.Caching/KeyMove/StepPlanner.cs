using System.Globalization;

namespace UiPath.Caching.KeyMove;

/// <summary>The leader's step logic as a function of what it read: the next transition, a wait with its alert, and the side effects. It never advances without the flag.</summary>
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
        var (driftPlan, driftAlerts) = CacheDrift(i);
        var plan = driftPlan ?? (state.Field(StateFields.ColdStart).Length > 0 ? new StepPlan(StepKind.ColdStart) : await StageStepAsync(i, options, side).ConfigureAwait(false));
        return plan with
        {
            Alerts = [.. plan.AlertList, .. side.Alerts, .. driftAlerts],
            AutoHoldSet = side.HoldSet,
            AutoHoldClear = side.HoldClear,
            AllAck = side.AllAck,
            Fields = [.. plan.FieldList, .. side.Fields],
        };
    }

    /// <summary>The flag value a state stands for: 2 for off, 3 for preparing to copying, 4 for copied, 5 for done.</summary>
    public static int StageFlagOf(StateRecord state) => state.State switch
    {
        KeyMoveStates.Copied => 4,
        KeyMoveStates.Done => 5,
        KeyMoveStates.Off => 2,
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
        return StepPlan.Wait(StagePending, $"flag_view:{nodes}");
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
