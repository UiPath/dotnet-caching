using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Polly;

internal sealed class ResiliencePipelineWrapper(ResiliencePipeline pipeline) : IResiliencePipeline
{
    public void Execute(Action callback) => pipeline.Execute(callback);

    public void Execute(Action<CancellationToken> callback, CancellationToken cancellationToken = default) => pipeline.Execute(callback, cancellationToken);

    public TResult Execute<TResult>(Func<CancellationToken, TResult> callback, CancellationToken cancellationToken = default) => pipeline.Execute(callback, cancellationToken);

    public TResult Execute<TResult>(Func<TResult> callback) => pipeline.Execute(callback);

    public ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> callback, CancellationToken cancellationToken = default) => pipeline.ExecuteAsync(callback, cancellationToken);

    public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, CancellationToken cancellationToken = default) => pipeline.ExecuteAsync(callback, cancellationToken);
}
