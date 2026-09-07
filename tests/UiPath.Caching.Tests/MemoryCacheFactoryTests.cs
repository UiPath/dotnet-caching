using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace UiPath.Caching.Tests;
public class MemoryCacheFactoryTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public void MemoryCacheFactory_CanCreateMemoryCache_with_size_limit()
    {
        // Arrange
        var clock = _fixture.Freeze<ISystemClock>();
        var factory = new MemoryCacheFactory(new SystemClockTimeProvider(clock), NullLoggerFactory.Instance);
        var memoryOptions = new MemoryCacheOptions
        {
            SizeLimit = 1,
            CompactionPercentage = 0.1
        };
        // Act
        var memoryCache = factory.Get(memoryOptions);
        memoryCache.Should().NotBeNull();
        var act = () => memoryCache.Set("testKey", "testValue",new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MemoryCacheFactory_CanCreateMemoryCache_with_size_limit_set()
    {
        // Arrange
        var clock = _fixture.Freeze<ISystemClock>();
        var factory = new MemoryCacheFactory(new SystemClockTimeProvider(clock), NullLoggerFactory.Instance);
        var memoryOptions = new MemoryCacheOptions
        {
            SizeLimit = 1,
            CompactionPercentage = 0.1
        };
        // Act
        var memoryCache = factory.Get(memoryOptions);
        memoryCache.Should().NotBeNull();
        var act = () => memoryCache.Set("testKey", "testValue", new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(5),
            Size = 1
        });
        act.Should().NotThrow();
    }

    [Fact]
    public void MemoryCacheFactory_CanCreateMemoryCache_with_size_no_limit()
    {
        // Arrange
        var clock = _fixture.Freeze<ISystemClock>();
        var factory = new MemoryCacheFactory(new SystemClockTimeProvider(clock), NullLoggerFactory.Instance);
        var memoryOptions = new MemoryCacheOptions
        {
        };
        // Act
        var memoryCache = factory.Get(memoryOptions);
        memoryCache.Should().NotBeNull();
        var act = () => memoryCache.Set("testKey", "testValue", new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        act.Should().NotThrow();
    }

    [Fact]
    public void The_memory_cache_judges_deadlines_on_the_injected_clock()
    {
        IsLiveUnder(Before).Should().BeTrue("the clock is a year before the deadline");
        IsLiveUnder(After).Should().BeFalse("the clock is a year past the deadline");
    }

    private static readonly DateTimeOffset Deadline = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ISystemClock Before = new FakeClock(Deadline.AddYears(-1));
    private static readonly ISystemClock After = new FakeClock(Deadline.AddYears(1));

    private static bool IsLiveUnder(ISystemClock clock)
    {
        var cache = new MemoryCacheFactory(new SystemClockTimeProvider(clock), NullLoggerFactory.Instance).Get(new MemoryCacheOptions());
        cache.Set("k", "v", new MemoryCacheEntryOptions { AbsoluteExpiration = Deadline });
        return cache.TryGetValue("k", out _);
    }

    private sealed class FakeClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    public class MemoryCacheOptions : IMemoryCacheOptions
    {
        public bool TrackStatistics { get; set; }
        
        public long? SizeLimit { get; set; }

        public double? CompactionPercentage { get; set; }

        public TimeSpan StatisticsFlushInterval { get; set; } = TimeSpan.FromMinutes(1);

        public ICacheEntrySizeProvider? SizeProvider { get; set; }
    }
}
