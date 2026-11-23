namespace UiPath.Caching.KeyMove;

internal sealed record PreflightResult(IReadOnlyList<PreflightFailure> Failures)
{
    public bool Ok => Failures.Count == 0;
}
