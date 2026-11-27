using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Broadcast.Redis;
using UiPath.Caching.Negotiation;

namespace UiPath.Caching.Tests.Redis;

/// <summary>
/// Waits for a node's Streams probe to decide. Topics start on Streams without waiting, so on a server without Streams a write made before
/// the decision may return false, and a test that writes straight after creating its nodes would race the probe.
/// </summary>
public static class StreamsReady
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Starts the node's Streams probe, if its Streams provider is enabled, and waits for the decision.</summary>
    /// <returns>Whether the node's Streams topics stay on Streams: false when the server cannot run them or the node has no Streams provider.</returns>
    public static bool Await(IServiceProvider provider) =>
        provider.GetService<RedisStreamsTopicProvider>() is { Enabled: true } streams
        && streams.DecideAsync(Timeout).GetAwaiter().GetResult() == StreamsState.Supported;

    /// <inheritdoc cref="Await(IServiceProvider)"/>
    public static async Task<bool> AwaitAsync(IServiceProvider provider) =>
        provider.GetService<RedisStreamsTopicProvider>() is { Enabled: true } streams
        && await streams.DecideAsync(Timeout, TestContext.Current.CancellationToken) == StreamsState.Supported;

    /// <summary>
    /// Waits until every Pub/Sub channel the node's topics listen on has its subscription in place. Pub/Sub does not replay, so on a server
    /// without Streams an event sent before the peer's subscription is live is never heard; Streams topics replay it from their group.
    /// </summary>
    public static async Task AwaitSubscribedAsync(IServiceProvider provider)
    {
        if (provider.GetService<RedisPubSubTopicProvider>() is not IPubSubSubscriptions subscriptions)
        {
            return;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CheckSubscriptions()
        {
            if (subscriptions.Confirmed().Count >= subscriptions.Channels().Count)
            {
                ready.TrySetResult();
            }
        }

        void OnRestored(object? sender, EventArgs e) => CheckSubscriptions();
        var restored = (RedisPubSubTopicProvider)subscriptions;
        subscriptions.WhenConfirmed(CheckSubscriptions);
        restored.OnConnectionRestored += OnRestored;
        try
        {
            CheckSubscriptions();
            await ready.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            restored.OnConnectionRestored -= OnRestored;
        }
    }
}
