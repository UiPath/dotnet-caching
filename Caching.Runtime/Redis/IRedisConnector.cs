namespace UiPath.Platform.Caching.Redis;

public interface IRedisConnector : IConnectionState, IDisposable
{
    Version Version { get; }

    IDatabase Database { get; }

    ISubscriber Subscriber { get; }

    void ForceReconnect();
}
