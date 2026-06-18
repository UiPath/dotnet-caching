using System.Collections.Concurrent;
using UiPath.Caching.Policies;

namespace UiPath.Caching.Polly;

internal sealed class ResiliencePipelineProvider(IResiliencePipelineFactory factory, ResiliencePipelineRegistry registry) : IResiliencePipelineProvider
{
    private readonly ConcurrentDictionary<string, IResiliencePipeline> _pipelines = new(StringComparer.Ordinal);

    public IResiliencePipeline Get(string? name) =>
        !string.IsNullOrEmpty(name) && registry.Contains(name)
            ? _pipelines.GetOrAdd(name, scope => new ResiliencePipelineWrapper(factory, scope))
            : EmptyResiliencePipeline.Instance;
}
