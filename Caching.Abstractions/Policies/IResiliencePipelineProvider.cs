namespace UiPath.Platform.Caching.Policies;

public interface IResiliencePipelineProvider
{
    IResiliencePipeline Get(string? name);
}
