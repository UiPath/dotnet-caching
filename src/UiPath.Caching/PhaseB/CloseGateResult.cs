namespace UiPath.Caching.PhaseB;

internal sealed record CloseGateResult(List<GateFailure> Failures, List<string> Overrides);
