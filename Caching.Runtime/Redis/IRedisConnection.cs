namespace UiPath.Platform.Caching.Redis;

public interface IRedisConnection : IDisposable
{
    public IConnectionMultiplexer Connection { get; }
}
