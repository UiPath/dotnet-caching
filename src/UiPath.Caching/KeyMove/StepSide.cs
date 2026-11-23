namespace UiPath.Caching.KeyMove;

internal sealed record StepSide(string? HoldSet, bool HoldClear, bool AllAck, IReadOnlyList<(string Field, string Value)> Fields, IReadOnlyList<StageAlert> Alerts, string? Hold);
