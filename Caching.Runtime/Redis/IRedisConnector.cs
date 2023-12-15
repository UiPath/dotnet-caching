namespace UiPath.Platform.Caching.Redis;

public interface IRedisConnector : IDisposable
{
    event EventHandler? OnReconnect;

    Version Version { get; }

    IDatabase Database { get; }

    ISubscriber Subscriber { get; }

    void ForceReconnect();
}
