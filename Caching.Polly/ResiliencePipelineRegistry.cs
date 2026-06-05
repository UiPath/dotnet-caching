namespace UiPath.Platform.Caching.Polly;

/// <summary>
/// Tracks the resilience pipeline names registered via
/// <see cref="CachingBuilderExtensions.AddResiliencePipeline"/> (including the predefined
/// <see cref="Policies.ResiliencePipelineNames.Read"/> / <see cref="Policies.ResiliencePipelineNames.Write"/>).
/// The <see cref="ResiliencePipelineProvider"/> only materializes a pipeline for a registered name;
/// any other name resolves to the no-op pipeline.
/// </summary>
internal sealed class ResiliencePipelineRegistry
{
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    public void Add(string name) => _names.Add(name);

    public bool Contains(string name) => _names.Contains(name);
}
