using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Tests.Redis;

public class RedisConnectionTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private RedisConnectionOptions redisOptions = default!;
    private IOptions<RedisConnectionOptions> optionsAccessor = default!;
    private IConnectionMultiplexer multiplexer = default!;
    private Func<ConfigurationOptions, IConnectionMultiplexer> multiplexerBuilder = default!;
    private ILogger<RedisConnection> logger = default!;

    [Fact]
    public void NotNullConnection()
    {
        var conection = new RedisConnection(optionsAccessor, multiplexerBuilder, logger);
        conection.Connection.Should().NotBeNull();
        conection.Dispose();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        redisOptions = _fixture.Create<RedisConnectionOptions>();
        optionsAccessor = Options.Create(redisOptions);
        multiplexer = _fixture.Create<IConnectionMultiplexer>();
        multiplexerBuilder = (config) => multiplexer;
        logger = _fixture.Create<ILogger<RedisConnection>>();
        return Task.CompletedTask;
    }
}
