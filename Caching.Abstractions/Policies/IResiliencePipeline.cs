namespace UiPath.Platform.Caching.Policies;

public interface IResiliencePipeline
{
    void Execute(Action callback);

    void Execute(Action<CancellationToken> callback, CancellationToken cancellationToken = default);

    TResult Execute<TResult>(Func<CancellationToken, TResult> callback, CancellationToken cancellationToken = default);

    TResult Execute<TResult>(Func<TResult> callback); 

    ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> callback, CancellationToken cancellationToken = default);

    ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, CancellationToken cancellationToken = default);
}
