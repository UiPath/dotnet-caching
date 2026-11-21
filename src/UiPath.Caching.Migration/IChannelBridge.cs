namespace UiPath.Caching.Migration;

/// <summary>
/// The application's adapter over its older client's invalidation channel, so nodes on the older client and nodes on the router hear each other.
/// The router publishes every key write, delete and clear through it and evicts on what it delivers; it needs no started library.
/// </summary>
public interface IChannelBridge
{
    /// <summary>Publishes the message in the older client's format. Carry <see cref="BridgeMessage.Origin"/> if the format can, so the router ignores its own messages.</summary>
    ValueTask PublishAsync(BridgeMessage message, CancellationToken cancellationToken);

    /// <summary>Delivers every received message to <paramref name="onMessage"/>, which only evicts and returns at once; disposing the result stops delivery.</summary>
    IDisposable Subscribe(Action<BridgeMessage> onMessage);
}
