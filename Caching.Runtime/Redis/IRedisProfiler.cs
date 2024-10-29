using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Redis;

public interface IRedisProfiler
{
    int Count { get; }

    ProfilingSession? GetSession(string? sessionId = null);

    IDisposable CreateSession(string? sessionId = null);
}
