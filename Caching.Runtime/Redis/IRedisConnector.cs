namespace UiPath.Platform.Caching.Redis;

public interface IRedisConnector : IConnectionState
{
    Version Version { get; }

    IDatabase Database { get; }

    ISubscriber Subscriber { get; }

    void ForceReconnect();
}
