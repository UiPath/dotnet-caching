using StackExchange.Redis;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Tests.Redis;

public class RedisConnectorTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private ICachingTelemetryProvider _telemetryProvider = default!;
    private IConnectionMultiplexer _multiplexer = default!;
    private Func<IConnectionMultiplexer> _multiplexerBuilder = default!;
    private IOptions<RedisConnectionOptions> _redisOptions = default!;
    private readonly string _connectionString = "localhost:6379";

    [Fact]
    public void NotNullConnection()
    {
        var connector = new RedisConnector(_telemetryProvider, _multiplexerBuilder, _redisOptions);
        connector.Database.Should().NotBeNull();
        connector.Subscriber.Should().NotBeNull();
        connector.Dispose();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _telemetryProvider = _fixture.Create<ICachingTelemetryProvider>();
        _multiplexer = _fixture.Create<IConnectionMultiplexer>();
        _multiplexerBuilder = () => _multiplexer;
        _redisOptions = Options.Create(new RedisConnectionOptions
        {
            ConnectionString = _connectionString
        });
        return Task.CompletedTask;
    }
}
