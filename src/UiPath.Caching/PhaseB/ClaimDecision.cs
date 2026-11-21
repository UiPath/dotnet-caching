namespace UiPath.Caching.PhaseB;

/// <summary>A script's code and the writes it makes, in order.</summary>
internal sealed record ClaimDecision(long Code, IReadOnlyList<ClaimWrite> Writes)
{
    public static ClaimDecision Of(long code) => new(code, []);

    public static ClaimDecision One(string field, string value) => new(ClaimCodes.Done, [ClaimWrite.Set(field, value)]);

    /// <summary>The "already done" branch: the field is written again with its current value, so a WAIT after it confirms the first write.</summary>
    public static ClaimDecision Touch(string field, ClaimValue value) => new(ClaimCodes.AlreadyDone, [ClaimWrite.Set(field, value.Text)]);
}
