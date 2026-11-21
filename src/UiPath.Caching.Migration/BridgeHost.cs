using Microsoft.Extensions.Logging;

namespace UiPath.Caching.Migration;

/// <summary>
/// The router's side of the bridge. No adapter failure reaches a caller or the host: a subscribe that throws is retried in the background, a publish
/// runs off the caller's thread and is cancelled at the source timeout, at most <see cref="MaxPending"/> publishes are in flight (more are dropped and
/// counted), and the local cap bounds how long a peer serves its copy. A received message only evicts; one carrying this node's identity is dropped.
/// </summary>
internal sealed class BridgeHost(IChannelBridge bridge, string origin, RouterCore core, ILogger logger)
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LastRetry = TimeSpan.FromMinutes(1);

    private IDisposable? _subscription;
    private int _pending;
    private int _publishFailureLogged;
    private int _detached;

    public string Origin { get; } = origin;

    /// <summary>Adapter calls that may be running, including ones a caller stopped waiting for; publishes beyond it are dropped.</summary>
    public int MaxPending { get; init; } = 64;

    /// <summary>Subscribes now, or keeps retrying in the background with backoff until it works or the router stops.</summary>
    public void Attach(Func<string, IRouterParticipant?> find, Action clearAll)
    {
        void OnMessage(BridgeMessage message) => Receive(message, find, clearAll);
        if (!TrySubscribe(OnMessage))
        {
            _ = Task.Run(() => RetrySubscribeAsync(OnMessage), CancellationToken.None);
        }
    }

    /// <summary>A subscription that fails to dispose is counted and logged; the router's stop carries on.</summary>
    public void Detach()
    {
        Volatile.Write(ref _detached, 1);
        try
        {
            Interlocked.Exchange(ref _subscription, null)?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RouterTelemetry.CountBridge("detach_failed", BridgeMessageKind.Clear);
            logger.LogWarning(ex, "The bridge subscription could not be disposed.");
        }
    }

    public async ValueTask PublishAsync(BridgeMessageKind kind, string cacheName, string? key)
    {
        if (Interlocked.Increment(ref _pending) > MaxPending)
        {
            Interlocked.Decrement(ref _pending);
            RouterTelemetry.CountBridge("publish_dropped", kind);
            return;
        }

        var deadline = new CancellationTokenSource(core.SourceTimeout, core.Clock);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, core.StopToken);
        var message = new BridgeMessage(kind, cacheName, key, Origin: Origin);

        // On the thread pool, so an adapter that blocks before it returns cannot hold the caller past the deadline.
        var call = Task.Run(() => bridge.PublishAsync(message, linked.Token).AsTask(), CancellationToken.None);
        _ = call.ContinueWith(
            (_, state) =>
            {
                var (pending, owner) = ((CancellationTokenSource[], BridgeHost))state!;
                foreach (var source in pending)
                {
                    source.Dispose();
                }

                Interlocked.Decrement(ref owner._pending);
            },
            (new[] { linked, deadline }, this),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            await call.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RouterTelemetry.CountBridge("publish_failed", kind);
            if (Interlocked.Exchange(ref _publishFailureLogged, 1) == 0)
            {
                logger.LogWarning(ex, "A bridge publish failed; peers on the older client may serve their copy up to the local cap. Later failures are counted only.");
            }
        }
    }

    private bool TrySubscribe(Action<BridgeMessage> onMessage)
    {
        try
        {
            var subscription = bridge.Subscribe(onMessage);
            if (Volatile.Read(ref _detached) == 1)
            {
                subscription.Dispose();
                return true;
            }

            Volatile.Write(ref _subscription, subscription);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RouterTelemetry.CountBridge("subscribe_failed", BridgeMessageKind.Clear);
            logger.LogWarning(ex, "The bridge could not subscribe; retrying in the background.");
            return false;
        }
    }

    private async Task RetrySubscribeAsync(Action<BridgeMessage> onMessage)
    {
        var delay = FirstRetry;
        while (Volatile.Read(ref _detached) == 0 && !core.StopToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, core.Clock, core.StopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Volatile.Read(ref _detached) == 1 || TrySubscribe(onMessage))
            {
                return;
            }

            delay = delay * 2 < LastRetry ? delay * 2 : LastRetry;
        }
    }

    private void Receive(BridgeMessage message, Func<string, IRouterParticipant?> find, Action clearAll)
    {
        try
        {
            if (string.Equals(message.Origin, Origin, StringComparison.Ordinal))
            {
                RouterTelemetry.CountBridge("own", message.Kind);
                return;
            }

            switch (message.Kind)
            {
                case BridgeMessageKind.Key when message.CacheName is { } name && message.Key is { Length: > 0 } key && find(name) is { } cache:
                    cache.EvictLocal(key);
                    break;
                case BridgeMessageKind.Clear when message.CacheName is { } name && find(name) is { } cache:
                    cache.ResetLocal();
                    break;
                case BridgeMessageKind.Tag:
                    clearAll();
                    break;
                default:
                    RouterTelemetry.CountBridge("unknown", message.Kind);
                    return;
            }

            RouterTelemetry.CountBridge("evicted", message.Kind);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RouterTelemetry.CountBridge("receive_failed", message.Kind);
            logger.LogDebug(ex, "A bridge message could not be applied.");
        }
    }
}
