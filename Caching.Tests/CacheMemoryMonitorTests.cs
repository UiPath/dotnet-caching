using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Tests;
public class CacheMemoryMonitorTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ICachingTelemetryProvider _telemetryProvider = default!;
    private string _statsMetricName = default!;
    private TimeSpan _statisticsFlushInterval = default!;
    private MemoryCache _memoryCache = default!;
    private CacheMemoryMonitor? _sut;
    private CacheMemoryMonitor Sut => _sut ??= new CacheMemoryMonitor(_statsMetricName, _statisticsFlushInterval, _memoryCache, _telemetryProvider);

    [Fact]
    public async Task Works_as_expected()
    {
        _memoryCache.Set(_fixture.Create<string>(), _fixture.Create<object>());
        Func<Task> act = async () => await Sut.MonitorTask;
        await act.Should().NotCompleteWithinAsync(_statisticsFlushInterval.Multiply(5));
        Sut.Dispose();
        await act.Should().CompleteWithinAsync(_statisticsFlushInterval.Multiply(10));
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
