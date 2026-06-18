using StackExchange.Redis.Profiling;

namespace UiPath.Caching.Redis;

public interface IProfilingSessionCommandReader
{
    public ProfileInfo Get(ProfilingSession? session);
}
