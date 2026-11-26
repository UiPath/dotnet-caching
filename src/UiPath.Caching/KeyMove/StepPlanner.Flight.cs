namespace UiPath.Caching.KeyMove;

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
            (KeyMoveStates.NotStarted, _, KeyMoveStates.Ready, "fwd") => "X1",
            (KeyMoveStates.Ready, "fwd", KeyMoveStates.Barrier, "fwd") => "X2",
            (KeyMoveStates.Ready or KeyMoveStates.Barrier, "fwd", KeyMoveStates.Moving, "fwd") => "X3",
            (KeyMoveStates.Moving, "fwd", KeyMoveStates.Drained, "fwd") => "X4",
            (KeyMoveStates.Drained, "fwd", KeyMoveStates.Closed, _) => "X5",
            (KeyMoveStates.Ready or KeyMoveStates.Barrier, "fwd", KeyMoveStates.NotStarted, _) => "X6",
            (KeyMoveStates.Moving or KeyMoveStates.Drained, "fwd", KeyMoveStates.Ready, "rev") => "X7",
            (KeyMoveStates.Drained, "fwd", KeyMoveStates.Moving, "fwd") => "X13",
            _ => null,
        };
    }

    // A transition at dur=0: completed when it is still asked for or already a reverse, abandoned before the commit when the flag no longer asks for it.
    private static StepPlan InFlight(StepInput i, StageDriverOptions options)
    {
        var state = i.State;
        var skip = Approval.ValidFor(state, ApprovalKinds.SkipBarrier) is not null;
        var kind = Classify(state);

        // From the commit on nothing is called off: the transition completes whatever the flag shows.
        if (kind is not null && state.Field(StateFields.CommitBegun) != state.Id)
        {
            if (!StillAsked(i, kind))
            {
                return new StepPlan(StepKind.Abandon);
            }

            // A forward step before the commit needs evidence: a pod that has not read the flag, or no fresh advert at all, is no view at or above the stage.
            if (ForwardLevel(kind) is not null && (i.Fresh.Count == 0 || i.Fresh.Any(a => a.FlagOf(i.Group) is null)))
            {
                var nodes = string.Join(',', i.Fresh.Where(a => a.FlagOf(i.Group) is null).Select(a => a.Node));
                return StepPlan.Wait(StagePending, $"flag_view:{nodes}");
            }
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

    private static int? ForwardLevel(string kind) => kind switch
    {
        "X1" or "X2" or "X3" => 3,
        "X4" => 4,
        "X5" => 5,
        _ => null,
    };
}
