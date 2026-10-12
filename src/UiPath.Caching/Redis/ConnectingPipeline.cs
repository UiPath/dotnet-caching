using UiPath.Caching.Policies;

namespace UiPath.Caching.Redis;

/// <summary>Runs each command once the connector has connected, waiting for the first connect asynchronously inside the pipeline instead of letting <see cref="IRedisConnector.Database"/> block the thread on it.</summary>
internal sealed class ConnectingPipeline(IRedisConnector redis, IResiliencePipeline inner) : IResiliencePipeline
{
    public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default) =>
        inner.ExecuteAsync(
            static async (state, token) =>
            {
                await state.Redis.ConnectAsync(token).ConfigureAwait(false);
                return await state.Callback(token).ConfigureAwait(false);
            },
            (Redis: redis, Callback: callback),
            defaultValue,
            cancellationToken);

    public ValueTask<TResult> ExecuteAsync<TResult, TState>(Func<TState, CancellationToken, ValueTask<TResult>> callback, TState state, TResult defaultValue, CancellationToken cancellationToken = default) =>
        inner.ExecuteAsync(
            static async (args, token) =>
            {
                await args.Redis.ConnectAsync(token).ConfigureAwait(false);
                return await args.Callback(args.State, token).ConfigureAwait(false);
            },
            (Redis: redis, Callback: callback, State: state),
            defaultValue,
            cancellationToken);
}
