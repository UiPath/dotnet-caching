using System.Diagnostics.CodeAnalysis;

namespace UiPath.Platform.Caching.Policies;

[ExcludeFromCodeCoverage]
public class NoOpExecutor : IPolicyExecutor
{
    public NoOpExecutor()
    {
    }

    public Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> action) =>
        action();

    public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> action, CancellationToken cancellationToken) =>
        action(cancellationToken);
}
