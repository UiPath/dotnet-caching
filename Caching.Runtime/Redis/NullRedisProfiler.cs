using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Redis;

[ExcludeFromCodeCoverage]
public class NullRedisProfiler : IRedisProfiler
{
    public static readonly NullRedisProfiler Instance = new();

    public int Count => 0;

    public ProfilingSession? GetSession() => null;

    public IDisposable CreateSession(string? sessionId) => Disposable.Empty;
}
