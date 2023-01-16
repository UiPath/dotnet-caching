using System.Diagnostics.CodeAnalysis;

namespace UiPath.Platform.Caching.Redis;

[ExcludeFromCodeCoverage]
public class RedisLatchOptions
{
    public string? InstanceName { get; set; }

    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromHours(1);
}
