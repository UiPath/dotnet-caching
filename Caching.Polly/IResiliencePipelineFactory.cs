namespace UiPath.Platform.Caching.Polly;

public interface IResiliencePipelineFactory
{
    ResiliencePipeline<TResult> Create<TResult>(string? scope, TResult defaultValue);
}
