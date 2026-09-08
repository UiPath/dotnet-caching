namespace UiPath.Caching.Policies;

/// <remarks><see cref="ExecuteAsync"/> must await <c>callback</c> to completion even on timeout: the Redis tier hands the connection borrowed memory that the caller reclaims once the pipeline returns.</remarks>
public interface IResiliencePipeline
{
    ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default);
}
