namespace UiPath.Platform.Caching.Policies;

public interface IPolicyExecutor
{
    Task ExecuteAsync(Func<Task> action, CancellationToken token);

    Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> action, CancellationToken token);

    Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> action, CancellationToken token);
    
    Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken token);
}
