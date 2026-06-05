namespace UiPath.Platform.Caching.Policies;

[ExcludeFromCodeCoverage]
public sealed class EmptyResiliencePipelineProvider : IResiliencePipelineProvider
{
    public static readonly EmptyResiliencePipelineProvider Instance = new();

    public IResiliencePipeline Get(string? name) => EmptyResiliencePipeline.Instance;
}
