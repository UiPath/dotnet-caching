namespace UiPath.Caching.PhaseB;

/// <summary>One write of a claim script: set a field, or delete it.</summary>
internal readonly record struct ClaimWrite(string Field, string? Value)
{
    public static ClaimWrite Set(string field, string value) => new(field, value);

    public static ClaimWrite Delete(string field) => new(field, null);
}
