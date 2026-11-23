namespace UiPath.Caching.Negotiation;

/// <summary>What a self-check of <c>PUBSUB NUMSUB</c> found: whether its numbers are the clients subscribed to a channel.</summary>
internal enum NumsubCheck
{
    /// <summary>One subscriber showed as one, or the server refused the command, which the count handles on its own.</summary>
    Reliable,

    /// <summary>One subscriber showed as more than one: the server answers with something that is not a client count, a proxy for one.</summary>
    NotClientCounts,

    /// <summary>The subscriber did not show within the wait, so nothing is known yet.</summary>
    Inconclusive,
}
