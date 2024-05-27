using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Polly;

internal sealed class PolicyWrapper(IAsyncPolicy asyncPolicy) : IPolicyExecutor
{
    public Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> action, CancellationToken token) =>
        WrapWithCancellation(() => asyncPolicy.ExecuteAsync(action), token);

    public Task ExecuteAsync(Func<Task> action, CancellationToken token) =>
        WrapWithCancellation(() => asyncPolicy.ExecuteAsync(action), token);

    public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> action, CancellationToken token) =>
        asyncPolicy.ExecuteAsync(action, token);

    public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken token) =>
        asyncPolicy.ExecuteAsync(action, token);

    private static async Task<T> WrapWithCancellation<T>(Func<Task<T>> asyncMethod, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<T>();

        using (token.Register(() => tcs.TrySetCanceled(token)))
        {
            var task = asyncMethod();

            if (task == await Task.WhenAny(task, tcs.Task).ConfigureAwait(false))
            {
                try
                {
                    var result = await task.ConfigureAwait(false);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private static async Task WrapWithCancellation(Func<Task> asyncMethod, CancellationToken token)
    {
        var tcs = new TaskCompletionSource();

        using (token.Register(() => tcs.TrySetCanceled(token)))
        {
            var task = asyncMethod();

            if (task == await Task.WhenAny(task, tcs.Task).ConfigureAwait(false))
            {
                try
                {
                    await task.ConfigureAwait(false);
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }
        }

        await tcs.Task.ConfigureAwait(false);
    }
}
