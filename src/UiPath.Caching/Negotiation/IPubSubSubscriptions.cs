namespace UiPath.Caching.Negotiation;

/// <summary>The Pub/Sub channels this node's topics listen on, and which of them the server has confirmed a subscription to.</summary>
internal interface IPubSubSubscriptions
{
    /// <summary>The channels whose subscribers <c>PUBSUB NUMSUB</c> can count.</summary>
    IReadOnlyList<string> Channels();

    /// <summary>The channels it cannot count, because the strategy made them patterns or sharded channels: it counts classic literal channels only.</summary>
    IReadOnlyList<string> Uncountable();

    /// <summary>Each channel whose subscription is in place, with how many subscriptions of this node it holds.</summary>
    IReadOnlyDictionary<string, int> Confirmed();

    /// <summary>How many pattern subscriptions this node holds, which <c>PUBSUB NUMSUB</c> does not count and <c>PUBSUB NUMPAT</c> does.</summary>
    int Patterns();

    /// <summary>Calls <paramref name="handler"/> each time a subscription is confirmed, so the node can say so at once.</summary>
    void WhenConfirmed(Action handler);
}
