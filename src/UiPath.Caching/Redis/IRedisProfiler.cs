using StackExchange.Redis.Profiling;

namespace UiPath.Caching.Redis;

public interface IRedisProfiler
{
    int Count { get; }

    ProfilingSession? GetSession();

    IDisposable CreateSession(string? sessionId);
}
