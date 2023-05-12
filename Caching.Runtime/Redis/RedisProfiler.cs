using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Redis;

#nullable disable
public class RedisProfiler : IRedisProfiler
{
    private readonly AsyncLocal<ProfilingSession> _asyncContextSession = new();
    private readonly ILogger<RedisProfiler> _logger;

    public RedisProfiler(ILogger<RedisProfiler> logger) =>
        _logger = logger;

    public bool CreateSession()
    {
        var val = _asyncContextSession.Value;
        if (val != null)
        {
            return false;
        }

        _asyncContextSession.Value = new ProfilingSession();
        return true;
    }

    public IEnumerable<IProfiledCommand> EndSession()
    {
        try
        {
            var session = _asyncContextSession.Value;
            if (session == null)
            {
                return Enumerable.Empty<IProfiledCommand>();
            }
            _asyncContextSession.Value = null;
            return session.FinishProfiling();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read profiled commands.");
            return Enumerable.Empty<IProfiledCommand>();
        }
    }

    public ProfilingSession GetSession() =>
        _asyncContextSession.Value;
}
