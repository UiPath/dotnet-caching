namespace UiPath.Platform.Caching.Policies;

[ExcludeFromCodeCoverage]
public class NoOpExecutor : IPolicyExecutor
{
    public Task ExecuteAsync(Func<Task> action, CancellationToken token) =>
        action();

    public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken token) =>
        action(token);

    public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> action, CancellationToken token) =>
        action(token);

    public Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> action, CancellationToken token) =>
        action();
}
