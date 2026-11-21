namespace UiPath.Caching.Tests.PhaseB.Harness;

internal readonly record struct CapResult(long Code, string? Token, bool DestinationExists, string? Gate, long Tcap);
