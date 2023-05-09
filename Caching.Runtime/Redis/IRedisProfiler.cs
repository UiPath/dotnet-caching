using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Redis;

public interface IRedisProfiler
{
    bool CreateSession();

    ProfilingSession GetSession();

    IEnumerable<IProfiledCommand> EndSession();
}
