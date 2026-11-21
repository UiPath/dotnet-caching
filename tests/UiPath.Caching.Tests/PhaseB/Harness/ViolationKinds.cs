namespace UiPath.Caching.Tests.PhaseB.Harness;

internal static class ViolationKinds
{
    public const string Stale = "stale-read";
    public const string FalseMiss = "false-miss";
    public const string Outlives = "outlives-source-deadline";
    public const string CaptureBound = "capture-bound";
    public const string PartialHash = "partial-hash";
}
