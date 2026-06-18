namespace UiPath.Caching.Policies;

public interface IResiliencePipelineProvider
{
    IResiliencePipeline Get(string? name);
}
