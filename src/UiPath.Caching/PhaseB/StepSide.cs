namespace UiPath.Caching.PhaseB;

/// <summary>The side effects of a step on a durable state: the automatic hold, the stored acknowledgement, the field repairs, and the hold in force.</summary>
internal sealed record StepSide(string? HoldSet, bool HoldClear, bool AllAck, IReadOnlyList<(string Field, string Value)> Fields, IReadOnlyList<StageAlert> Alerts, string? Hold);
