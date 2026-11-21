using Microsoft.Extensions.Logging;

namespace UiPath.Caching.Migration;

/// <summary>
/// The router's side of the bridge. A publish is bounded by the source timeout and its failure is counted, never thrown: the local cap bounds how long a
/// peer can serve its copy. A received message only evicts, so nothing it does publishes again, and a message carrying this node's identity is dropped.
/// </summary>
internal sealed class BridgeHost(IChannelBridge bridge, string origin, RouterCore core, ILogger logger)
{
    private IDisposable? _subscription;
    private int _publishFailureLogged;

    public string Origin { get; } = origin;

    public void Attach(Func<string, IRouterParticipant?> find, Action clearAll)
    {
        _subscription = bridge.Subscribe(message => Receive(message, find, clearAll));
    }

    public void Detach() => Interlocked.Exchange(ref _subscription, null)?.Dispose();

    public async ValueTask PublishAsync(BridgeMessageKind kind, string cacheName, string? key)
    {
        try
        {
            await bridge.PublishAsync(new BridgeMessage(kind, cacheName, key, Origin: Origin), core.StopToken).AsTask()
                .WaitAsync(core.SourceTimeout, core.Clock, core.StopToken).ConfigureAwait(false);
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
