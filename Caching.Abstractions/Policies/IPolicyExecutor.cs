namespace UiPath.Platform.Caching.Policies;

public interface IPolicyExecutor
{
    /// <summary>
    ///     Executes the specified asynchronous action and returns the result.
    /// </summary>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <param name="action">The action to perform.</param>
    /// <returns>The value returned by the action</returns>
    Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> action);

    /// <summary>
    ///     Executes the specified asynchronous action and returns the result.
    /// </summary>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <param name="action">The action to perform.</param>
    /// <param name="cancellationToken">A cancellation token which can be used to cancel the action.  When a retry policy is in use, also cancels any further retries.</param>
    /// <returns>The value returned by the action</returns>
    Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> action, CancellationToken cancellationToken);
}
