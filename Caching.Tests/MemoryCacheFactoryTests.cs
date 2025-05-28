using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace UiPath.Platform.Caching.Tests;
public class MemoryCacheFactoryTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public void MemoryCacheFactory_CanCreateMemoryCache_with_size_limit()
    {
        // Arrange
        var clock = _fixture.Freeze<ISystemClock>();
        var factory = new MemoryCacheFactory(clock, NullLoggerFactory.Instance);
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
        var factory = new MemoryCacheFactory(clock, NullLoggerFactory.Instance);
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
        var factory = new MemoryCacheFactory(clock, NullLoggerFactory.Instance);
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

    public class MemoryCacheOptions : IMemoryCacheOptions
    {
        public bool TrackStatistics { get; set; }
        
        public long? SizeLimit { get; set; }

        public double? CompactionPercentage { get; set; }

        public TimeSpan StatisticsFlushInterval { get; set; } = TimeSpan.FromMinutes(1);

        public ICacheEntrySizeProvider? SizeProvider { get; set; }
    }
}
