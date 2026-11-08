using UiPath.Caching.Policies;

namespace UiPath.Caching.Redis;

/// <summary>Runs each command once the connector has connected, waiting for the first connect asynchronously inside the pipeline instead of letting <see cref="IRedisConnector.Database"/> block the thread on it.</summary>
internal sealed class ConnectingPipeline(IRedisConnector redis, IResiliencePipeline inner) : IResiliencePipeline
{
    public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default) =>
        inner.ExecuteAsync(
            static (state, token) =>
            {
                // Connected, the command runs in this frame: an async one here would cost every command a state machine.
                if (!TryConnect(state.Redis, token, out var pending, out var connected, out var failure))
                {
                    return FailAsync<TResult>(failure);
                }

                return connected ? Run(state.Callback, token) : ConnectThenRunAsync(pending, state.Callback, token);
            },
            (Redis: redis, Callback: callback),
            defaultValue,
            cancellationToken);

    public ValueTask<TResult> ExecuteAsync<TResult, TState>(Func<TState, CancellationToken, ValueTask<TResult>> callback, TState state, TResult defaultValue, CancellationToken cancellationToken = default) =>
        inner.ExecuteAsync(
            static (args, token) =>
            {
                if (!TryConnect(args.Redis, token, out var pending, out var connected, out var failure))
                {
                    return FailAsync<TResult>(failure);
                }

                return connected ? Run(args.Callback, args.State, token) : ConnectThenRunAsync(pending, args.Callback, args.State, token);
            },
            (Redis: redis, Callback: callback, State: state),
            defaultValue,
            cancellationToken);

    /// <summary>Starts the connect; a connector that throws instead of returning a faulted task fails the command the same way.</summary>
    /// <remarks>Decided once: a completed connect is consumed here and never looked at again, since a pooled source may be reused once consumed; one still running is handed back in <paramref name="pending"/> to be awaited once.</remarks>
    private static bool TryConnect(IRedisConnector redis, CancellationToken token, out ValueTask pending, out bool connected, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out Exception? failure)
    {
        pending = default;
        connected = false;
        failure = null;
        try
        {
            var connect = redis.ConnectAsync(token);
            if (connect.IsCompletedSuccessfully)
            {
                connect.GetAwaiter().GetResult();
                connected = true;
            }
            else
            {
                pending = connect;
            }

            return true;
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
    }

    // A command that throws before it returns fails its task, as it did inside the async frame this replaces.
    private static ValueTask<TResult> Run<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, CancellationToken token)
    {
        try
        {
            return callback(token);
        }
        catch (Exception ex)
        {
            return FailAsync<TResult>(ex);
        }
    }

    private static ValueTask<TResult> Run<TResult, TState>(Func<TState, CancellationToken, ValueTask<TResult>> callback, TState state, CancellationToken token)
    {
        try
        {
            return callback(state, token);
        }
        catch (Exception ex)
        {
            return FailAsync<TResult>(ex);
        }
    }

    // Through an async method, so a cancellation ends the task cancelled rather than faulted.
    private static async ValueTask<TResult> FailAsync<TResult>(Exception failure)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        return default;
    }

    private static async ValueTask<TResult> ConnectThenRunAsync<TResult>(ValueTask connect, Func<CancellationToken, ValueTask<TResult>> callback, CancellationToken token)
    {
        await connect.ConfigureAwait(false);
        return await callback(token).ConfigureAwait(false);
    }

    private static async ValueTask<TResult> ConnectThenRunAsync<TResult, TState>(ValueTask connect, Func<TState, CancellationToken, ValueTask<TResult>> callback, TState state, CancellationToken token)
    {
        await connect.ConfigureAwait(false);
        return await callback(state, token).ConfigureAwait(false);
    }
}
