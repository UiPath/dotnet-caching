namespace UiPath.Caching.Tests.PhaseB.Harness;

/// <param name="Value">The version written, or null for a delete.</param>
/// <param name="Px">The relative life of the written value, or -1 for none.</param>
internal sealed record WriterOp(string? Value, long Px = -1)
{
    public static WriterOp Delete { get; } = new((string?)null);

    public bool IsDelete => Value is null;

    public static WriterOp Set(string value, long px = -1) => new(value, px);
}
