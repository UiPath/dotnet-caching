using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Redis;

public interface IRedisProfiler
{
    int Count { get; }

    ProfilingSession? GetSession();

    IDisposable CreateSession(string? sessionId);
}
