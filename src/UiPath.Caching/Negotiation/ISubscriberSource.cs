using System.Net;

namespace UiPath.Caching.Negotiation;

/// <summary>The servers of the Redis deployment, and how many subscribers each one counts on Pub/Sub channels and patterns.</summary>
internal interface ISubscriberSource
{
    /// <summary>
    /// Every server the deployment has, primaries and replicas alike, since a subscriber is local to the server it connected to. It throws when it cannot
    /// say that it has them all, a server that is not connected for one: a sum over some of the servers would hide the subscribers of the others.
    /// </summary>
    IReadOnlyList<EndPoint> Endpoints();

    /// <summary>The server's <c>PUBSUB NUMSUB</c> for all the channels, in the order asked, and its <c>PUBSUB NUMPAT</c>; <see langword="null"/> when the server refuses either.</summary>
    Task<ServerCount?> CountAsync(EndPoint endpoint, IReadOnlyList<string> channels, CancellationToken cancellationToken);
}
