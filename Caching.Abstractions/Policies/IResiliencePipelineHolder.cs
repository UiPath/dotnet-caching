namespace UiPath.Platform.Caching.Policies;

public interface IResiliencePipelineHolder
{
    IResiliencePipeline Read { get; }

    IResiliencePipeline Write { get; }
}
