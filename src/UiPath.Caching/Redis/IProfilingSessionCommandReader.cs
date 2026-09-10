using StackExchange.Redis.Profiling;

namespace UiPath.Caching.Redis;

public interface IProfilingSessionCommandReader
{
    ProfileInfo Get(ProfilingSession? session);
}
