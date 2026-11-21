namespace UiPath.Caching.PhaseB;

internal enum StepKind
{
    /// <summary>Nothing to do this step, besides the side effects of the plan.</summary>
    None,

    /// <summary>Start the transition of the plan's spec.</summary>
    Transit,

    /// <summary>X12 toward the last committed state; when it is refused because step 4 began, the transition is completed instead.</summary>
    Abandon,

    /// <summary>Steps 2 to 6 of the stored transition.</summary>
    Resume,

    /// <summary>X11: the operator's abandon of a closed group.</summary>
    ColdStart,
}
