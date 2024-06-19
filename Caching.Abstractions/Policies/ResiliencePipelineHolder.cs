namespace UiPath.Platform.Caching.Policies;

public sealed class ResiliencePipelineHolder(IResiliencePipeline read, IResiliencePipeline? write = null) : IResiliencePipelineHolder
{
    public static readonly ResiliencePipelineHolder Empty = new(new EmptyResiliencePipeline());

    public IResiliencePipeline Read { get; } = read;

    public IResiliencePipeline Write { get; } = write ?? read;
}
