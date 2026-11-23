namespace UiPath.Caching.KeyMove;

internal sealed record CloseGateResult(List<GateFailure> Failures, List<string> Overrides);
