namespace UiPath.Caching.Migration;

/// <summary>What <see cref="RouterConformance.CheckAsync{T}"/> found; a failure names the sample by position, never by key or value.</summary>
public sealed class ConformanceReport
{
    internal ConformanceReport(int samples, IReadOnlyList<string> failures)
    {
        Samples = samples;
        Failures = failures;
    }

    public int Samples { get; }

    public IReadOnlyList<string> Failures { get; }

    public bool Passed => Failures.Count == 0;

    public override string ToString() =>
        Passed ? $"{Samples} samples conform." : $"{Failures.Count} failures over {Samples} samples: {string.Join(" ", Failures)}";
}
