using System.Diagnostics.CodeAnalysis;
using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Redis;

[ExcludeFromCodeCoverage]
public class RedisConnectionOptions
{
    public string ConnectionString { get; set; } = default!;

    public int? BackOffMilliseconds { get; set; }

    public TimeSpan? HeartbeatInterval { get; set; }

    public Func<ProfilingSession>? ProfilingSession { get; set; }
}
