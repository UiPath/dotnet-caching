using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Polly;

public class PollyHolder : IPolicyHolder
{
    public PollyHolder(IAsyncPolicy[] read, IAsyncPolicy[] write)
    {
        Read = new PolicyWrapper(Build(read));
        Write = new PolicyWrapper(Build(write));
    }

    public IPolicyExecutor Read { get; }

    public IPolicyExecutor Write { get; }

    private static IAsyncPolicy Build(IAsyncPolicy[] policies)
    {
        if (policies.Length == 0)
        {
            return Policy.NoOpAsync();
        }

        if (policies.Length == 1)
        {
            return policies[0];
        }

        return Policy.WrapAsync(policies);
    }

    private sealed class PolicyWrapper : IPolicyExecutor
    {
        private readonly IAsyncPolicy _asyncPolicy;

        public PolicyWrapper(IAsyncPolicy asyncPolicy) =>
            _asyncPolicy = asyncPolicy;

        public Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> action) =>
            _asyncPolicy.ExecuteAsync(action);

        public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> action, CancellationToken cancellationToken) =>
            _asyncPolicy.ExecuteAsync(action, cancellationToken);
    }
}
