namespace UiPath.Caching.Migration;

/// <summary>
/// The application's adapter over its older client's invalidation channel, so nodes on the older client and nodes on the router hear each other.
/// The router publishes every key write, delete and clear through it and evicts on what it delivers; it needs no started library.
/// </summary>
public interface IChannelBridge
{
    /// <summary>
    /// Publishes the message in the older client's format. It must not block: return a task at once and honour <paramref name="cancellationToken"/>, which
    /// the router cancels at its source timeout. Carry <see cref="BridgeMessage.Origin"/> if the format can, so the router ignores its own messages.
    /// </summary>
    ValueTask PublishAsync(BridgeMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Delivers every received message to <paramref name="onMessage"/>, which only evicts and returns at once and may be called from several threads.
    /// Disposing the result stops delivery; a message delivered after that is ignored. A Subscribe that throws is called again later.
    /// </summary>
    IDisposable Subscribe(Action<BridgeMessage> onMessage);
}
