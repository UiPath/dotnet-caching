namespace UiPath.Caching.KeyMove;

/// <summary>A null <see cref="Value"/> deletes the field; the fallback queues all writes of a decision in one watched transaction.</summary>
internal readonly record struct ClaimWrite(string Field, string? Value)
{
    public static ClaimWrite Set(string field, string value) => new(field, value);

    public static ClaimWrite Delete(string field) => new(field, null);
}
