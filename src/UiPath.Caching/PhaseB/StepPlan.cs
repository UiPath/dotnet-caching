namespace UiPath.Caching.PhaseB;

/// <summary>One decision of the leader for one group, with the side effects that go with it.</summary>
/// <param name="AutoHoldSet">Reason for a new <c>hold:auto</c>.</param>
/// <param name="AllAck">Store <c>allAck</c>: every fresh node acknowledged the stored identity.</param>
/// <param name="Caches">For <see cref="StepKind.RebuildCaches"/>, the encoded managed set to write.</param>
internal sealed record StepPlan(
    StepKind Kind,
    TransitionSpec? Spec = null,
    bool SkipBarriers = false,
    IReadOnlyList<StageAlert>? Alerts = null,
    string? AutoHoldSet = null,
    bool AutoHoldClear = false,
    IReadOnlyList<(string Field, string Value)>? Fields = null,
    bool AllAck = false,
    string? Caches = null)
{
    public IReadOnlyList<StageAlert> AlertList => Alerts ?? [];

    public IReadOnlyList<(string Field, string Value)> FieldList => Fields ?? [];

    public static StepPlan Nothing { get; } = new(StepKind.None);

    public static StepPlan Wait(string code, string detail) => new(StepKind.None, Alerts: [new StageAlert(code, detail)]);

    public StepPlan With(params StageAlert[] alerts) => this with { Alerts = [.. AlertList, .. alerts] };
}
