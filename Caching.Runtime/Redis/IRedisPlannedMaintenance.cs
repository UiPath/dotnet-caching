namespace UiPath.Platform.Caching.Redis;

public interface IRedisPlannedMaintenance : IDisposable
{
    bool InProgress { get; }
}
