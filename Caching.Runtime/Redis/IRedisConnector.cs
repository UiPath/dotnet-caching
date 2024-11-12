using System.Net;

namespace UiPath.Platform.Caching.Redis;

public interface IRedisConnector : IConnectionState, IDisposable
{
    Version Version { get; }

    IDatabase Database { get; }

    ISubscriber Subscriber { get; }

    void ForceReconnect();

    EndPoint[] GetEndPoints(bool configuredOnly = false);
}
