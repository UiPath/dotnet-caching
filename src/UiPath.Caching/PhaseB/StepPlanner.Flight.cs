namespace UiPath.Caching.PhaseB;

internal static partial class StepPlanner
{
    // Only transitions the flag can call off; the way back that has begun (X8 to X11) and a repair run to the end.
    internal static string? Classify(StateRecord state)
    {
        var prev = state.Prev.Split(':');
        if (prev.Length != 5)
        {
            return null;
        }

        return (prev[0], prev[1], state.State, state.Direction) switch
        {
            ("S0", _, "S1", "fwd") => "X1",
            ("S1", "fwd", "S2", "fwd") => "X2",
            ("S1" or "S2", "fwd", "S3", "fwd") => "X3",
            ("S3", "fwd", "S4", "fwd") => "X4",
            ("S4", "fwd", "S5", _) => "X5",
            ("S1" or "S2", "fwd", "S0", _) => "X6",
            ("S3" or "S4", "fwd", "S1", "rev") => "X7",
            ("S4", "fwd", "S3", "fwd") => "X13",
            _ => null,
        };
    }

    // A transition at dur=0: completed when it is still asked for or already a reverse, abandoned before step 4 when the flag no longer asks for it.
    private static StepPlan InFlight(StepInput i, StageDriverOptions options)
    {
        var state = i.State;
        var skip = Approval.ValidFor(state, ApprovalKinds.SkipBarrier) is not null;
        var kind = Classify(state);
        if (kind is not null && !StillAsked(i, kind))
        {
            return new StepPlan(StepKind.Abandon);
        }

        return i.DurZeroFor >= options.DurRetry
            ? new StepPlan(StepKind.Resume, SkipBarriers: skip, Alerts: [new StageAlert("transition_resumed", kind ?? "X12")])
            : StepPlan.Nothing;
    }

    private static bool StillAsked(StepInput i, string kind)
    {
        var views = i.Fresh.Select(a => a.FlagOf(i.Group)).Where(v => v is not null).Select(v => v!.Value).ToArray();
        return kind switch
        {
            "X1" or "X2" or "X3" => !views.Any(v => v < 3),
            "X4" => !views.Any(v => v < 4),
            "X5" => !views.Any(v => v < 5),
            "X6" or "X7" => views.Any(v => v <= 2),
            "X13" => views.Any(v => v == 3) && !views.Any(v => v <= 2),
            _ => true,
        };
    }
}
