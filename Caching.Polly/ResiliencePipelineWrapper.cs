using System.Collections.Concurrent;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Polly;

internal sealed class ResiliencePipelineWrapper(IResiliencePipelineFactory factory, string? scope) : IResiliencePipeline
{
    private readonly ConcurrentDictionary<(Type,object?), object> _cachePipeline = new();

    public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
    {
        var pipeline = GetPipeline(defaultValue);
        return pipeline.ExecuteAsync(callback, cancellationToken);
    }
    private ResiliencePipeline<TResult> GetPipeline<TResult>(TResult defaultValue)
    {
        var key = (typeof(TResult), defaultValue);
        var pipeline = _cachePipeline.GetOrAdd(key, _ => factory.Create(scope, defaultValue));
        return pipeline is ResiliencePipeline<TResult> p ? p : throw new InvalidOperationException($"Pipeline for {typeof(TResult)} not found.");
    }
}
