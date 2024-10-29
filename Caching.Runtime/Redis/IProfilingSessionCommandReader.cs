using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Redis;

public interface IProfilingSessionCommandReader
{
    public ProfileInfo Get(ProfilingSession? session);
}
