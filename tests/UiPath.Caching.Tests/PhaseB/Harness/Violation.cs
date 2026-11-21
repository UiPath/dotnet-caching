namespace UiPath.Caching.Tests.PhaseB.Harness;

internal sealed record Violation(string Kind, string Message, bool Final = false);
