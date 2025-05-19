namespace UiPath.Platform.Caching.Policies;

public interface IResiliencePipeline
{
    ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default);
}
