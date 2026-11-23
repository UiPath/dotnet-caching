namespace UiPath.Caching.KeyMove;

internal enum StepKind
{
    /// <summary>Nothing to do this step, besides the side effects of the plan.</summary>
    None,

    Transit,

    /// <summary>X12 toward the last committed state; when it is refused because the commit began, the transition is completed instead.</summary>
    Abandon,

    Resume,

    ColdStart,
}
