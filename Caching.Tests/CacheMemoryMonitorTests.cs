using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Tests;

public class CacheMemoryMonitorTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    
    private ICachingTelemetryProvider _telemetryProvider = default!;
    private string _statsMetricName = default!;
    private TimeSpan _statisticsFlushInterval = default!;
    private MemoryCache _memoryCache = default!;
    private CacheMemoryMonitor? _sut = null;
    private CacheMemoryMonitor Sut => _sut ??= new CacheMemoryMonitor(_statsMetricName, _statisticsFlushInterval, _memoryCache, _telemetryProvider); 

    [Fact]
    public async Task Works_as_expected()
    {
        _memoryCache.Set(_fixture.Create<string>(), _fixture.Create<object>());
        Sut.MonitorTaskStatus.Should().NotBe(TaskStatus.RanToCompletion);
        Sut.Dispose();
        await Task.Delay(_statisticsFlushInterval.Multiply(10));
        Sut.MonitorTaskStatus.Should().Be(TaskStatus.RanToCompletion);
    }

    [Fact]
    public async Task Dispose_works_as_expected()
    {
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
        await Task.Delay(_statisticsFlushInterval.Multiply(10));
        Sut.MonitorTaskStatus.Should().Be(TaskStatus.RanToCompletion);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _telemetryProvider = _fixture.Create<ICachingTelemetryProvider>();
        _statsMetricName = _fixture.Create<string>();
        _statisticsFlushInterval = TimeSpan.FromMilliseconds(100);
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            TrackStatistics = true,
            TrackLinkedCacheEntries = true,
            Clock = new SystemClock()
        }));
        return Task.CompletedTask;
    }
}
