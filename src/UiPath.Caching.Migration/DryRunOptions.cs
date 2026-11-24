namespace UiPath.Caching.Migration;

/// <summary>The settings of a dry run.</summary>
internal sealed class DryRunOptions
{
    /// <summary>Every key of a cache is decoded until this many were; after that, one in <see cref="SampleEvery"/>.</summary>
    public int DecodeCap { get; init; } = 10_000;

    public int SampleEvery { get; init; } = 100;
}
