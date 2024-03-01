namespace UiPath.Platform.Caching.Policies;

public interface IPolicyExecutor
{
    Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> action);

    Task ExecuteAsync(Func<Task> action);

    Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> action, CancellationToken cancellationToken);
    
    Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken);
}
