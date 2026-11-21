namespace UiPath.Caching.PhaseB;

internal sealed record GateFailure(string Gate, string Reason)
{
    public override string ToString() => $"{Gate}:{Reason}";
}
