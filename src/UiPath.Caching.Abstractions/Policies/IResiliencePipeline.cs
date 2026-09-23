namespace UiPath.Caching.Policies;

/// <remarks>The read pipeline returns when its token is cancelled, leaving <c>callback</c> running; others wait.
/// A callback holding memory the caller reclaims on return must not use the read pipeline.</remarks>
public interface IResiliencePipeline
{
    ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default);
}
