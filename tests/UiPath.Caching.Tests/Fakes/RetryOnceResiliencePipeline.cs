using StackExchange.Redis;
using UiPath.Caching.Policies;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>Replays the callback once after it throws.</summary>
internal sealed class RetryOnceResiliencePipeline : IResiliencePipeline
{
    public async ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
    {
        try
        {
            return await callback(cancellationToken).ConfigureAwait(false);
        }
        catch (RedisException)
        {
            return await callback(cancellationToken).ConfigureAwait(false);
        }
    }
}
