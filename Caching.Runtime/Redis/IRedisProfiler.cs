using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Redis;

public interface IRedisProfiler
{
    int Count { get; }

    ProfilingSession? GetSession();

    ProfilingSession? GetSession(string? sessionId);

    IDisposable CreateSession(string? sessionId = null);
}
