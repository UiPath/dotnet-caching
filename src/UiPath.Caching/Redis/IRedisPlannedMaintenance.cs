namespace UiPath.Caching.Redis;

public interface IRedisPlannedMaintenance : IDisposable
{
    bool InProgress { get; }
}
