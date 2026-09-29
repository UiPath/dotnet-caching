namespace UiPath.Caching.Policies;

/// <remarks>The read pipeline returns when its token is cancelled, leaving <c>callback</c> running; others wait.
/// A callback holding memory the caller reclaims on return must not use the read pipeline.</remarks>
public interface IResiliencePipeline
{
    ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default);

    /// <summary>Passes <paramref name="state"/> to a static <paramref name="callback"/>. This default forwards through a closure and a delegate per call, so implement it to avoid both.</summary>
    ValueTask<TResult> ExecuteAsync<TResult, TState>(Func<TState, CancellationToken, ValueTask<TResult>> callback, TState state, TResult defaultValue, CancellationToken cancellationToken = default) =>
        ExecuteAsync(token => callback(state, token), defaultValue, cancellationToken);
}
