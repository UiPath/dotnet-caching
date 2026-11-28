namespace UiPath.Caching.KeyMove;

/// <param name="NewMoveId">The transition writes <c>moveId</c> equal to its <c>ep</c> (X1, X7, X13).</param>
/// <param name="WayBack">A way-back transition: barriers of a master that already failed are skipped at once.</param>
/// <param name="TakeClaim">X1 of a group that takes the move claim.</param>
/// <param name="ReleaseClaim">A transition into not-started of a group that holds a claim: certificate before DURABLE, idle after.</param>
/// <param name="RecheckViews">The views are read again just before the commit, and the transition is abandoned if one fell (X5).</param>
/// <param name="Binding">What it does to the generations.</param>
/// <param name="PostFields">Fields written under the new identity once it is durable.</param>
/// <param name="PostDelete">Fields deleted under the new identity once it is durable.</param>
internal sealed record TransitionSpec(
    string Code,
    string State,
    string Direction,
    int NPop,
    bool NewMoveId = false,
    bool WayBack = false,
    bool TakeClaim = false,
    bool ReleaseClaim = false,
    PreStep Pre = PreStep.None,
    bool RecheckViews = false,
    BindingChange Binding = BindingChange.None,
    IReadOnlyList<(string Field, string Value)>? PostFields = null,
    IReadOnlyList<string>? PostDelete = null);
