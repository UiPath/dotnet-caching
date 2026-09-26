using System.Collections.Concurrent;
using UiPath.Caching.Policies;

namespace UiPath.Caching.Polly;

internal sealed class ResiliencePipelineWrapper(IResiliencePipelineFactory factory, string? scope) : IResiliencePipeline
{
    // One typed map per result type: a lookup neither boxes the default nor allocates a factory closure.
    private readonly ConcurrentDictionary<Type, object> _pipelinesByType = new();

    // Reads only: abandoning a write or an SPOP would lose data.
    private readonly bool _abandonOnCancellation = string.Equals(scope, ResiliencePipelineNames.Read, StringComparison.Ordinal);

    // Polly's timeout is cooperative, and the Redis client takes no token.
    public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
    {
        var pipeline = GetPipeline(defaultValue);
        return _abandonOnCancellation
            ? pipeline.ExecuteAsync(static (callback, token) => RaceCancellation(callback, token), callback, cancellationToken)
            : pipeline.ExecuteAsync(callback, cancellationToken);
    }

    /// <summary>Returns when the token fires, leaving the callback running.</summary>
    private static async ValueTask<TResult> RaceCancellation<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, CancellationToken token)
    {
        var pending = callback(token);
        if (pending.IsCompleted || !token.CanBeCanceled)
        {
            return await pending.ConfigureAwait(false);
        }

        var task = pending.AsTask();
        try
        {
            return await task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // WaitAsync stops observing the task.
            _ = task.ContinueWith(
                static abandoned => _ = abandoned.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    private ResiliencePipeline<TResult> GetPipeline<TResult>(TResult defaultValue)
    {
        var pipelines = (ConcurrentDictionary<DefaultValue<TResult>, ResiliencePipeline<TResult>>)_pipelinesByType.GetOrAdd(
            typeof(TResult),
            static _ => new ConcurrentDictionary<DefaultValue<TResult>, ResiliencePipeline<TResult>>());
        return pipelines.GetOrAdd(
            new DefaultValue<TResult>(defaultValue),
            static (key, state) => state.Factory.Create(state.Scope, key.Value),
            (Factory: factory, Scope: scope));
    }

    // Wrapped so a null default can still key the map.
    private readonly record struct DefaultValue<TResult>(TResult Value);
}
