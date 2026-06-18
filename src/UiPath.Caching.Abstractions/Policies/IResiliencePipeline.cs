namespace UiPath.Caching.Policies;

public interface IResiliencePipeline
{
    ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default);
}
