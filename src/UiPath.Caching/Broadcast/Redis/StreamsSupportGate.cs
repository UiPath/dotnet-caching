using Microsoft.Extensions.Hosting;

namespace UiPath.Caching.Broadcast.Redis;

/// <summary>
/// Fails host start, for <see cref="StreamsUnsupportedAction.Fail"/>, when a probe within its budget proves the server does not run
/// Streams. A server that does not answer in time never fails the start: the probe goes on, and the capability is logged when known.
/// </summary>
internal sealed class StreamsSupportGate(
    IServiceProvider services,
    IOptions<RedisStreamsTopicOptions> streamOptions,
    IOptions<CacheOptions> cacheOptions,
    ILoggerFactory loggerFactory) : IHostedService
{
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    internal TimeSpan ProbeBudget { get; set; } = Budget;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!cacheOptions.Value.BroadcastEnabled || !streamOptions.Value.Enabled || services.GetService<IRedisConnector>() is not { } connector)
        {
            return;
        }

        var capability = BroadcastCapability.For(connector);
        var settings = StreamsProbeSettings.For(streamOptions.Value, cacheOptions.Value, loggerFactory.CreateLogger<StreamsSupportGate>());
        _ = capability.Start(settings);
        if (await capability.WaitDecidedAsync(ProbeBudget, cancellationToken).ConfigureAwait(false) == StreamsState.Unsupported)
        {
            throw new InvalidOperationException(
                $"The Redis server does not support Redis Streams ({capability.Reason}) and {nameof(RedisStreamsTopicOptions.StreamsUnsupported)} is {nameof(StreamsUnsupportedAction.Fail)}. Set it to {nameof(StreamsUnsupportedAction.PubSub)} or {nameof(StreamsUnsupportedAction.Disable)}, or use a server that runs Streams.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
