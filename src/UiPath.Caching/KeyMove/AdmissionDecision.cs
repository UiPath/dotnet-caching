namespace UiPath.Caching.KeyMove;

internal sealed record AdmissionDecision(bool Admit, string? Alert = null, string Detail = "")
{
    public static AdmissionDecision Passed { get; } = new(true);

    public static AdmissionDecision Refuse(string alert, string detail) => new(false, alert, detail);
}
