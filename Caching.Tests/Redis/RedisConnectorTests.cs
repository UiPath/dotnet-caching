using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Tests.Redis;

public class RedisConnectorTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private ICachingTelemetryProvider _telemetryProvider = default!;
    private IRedisProfiler _profiler = default!;
    private IOptions<RedisConnectionOptions> _redisOptions = default!;
    private IRedisConfigurationOptionsProvider _redisConfigurationOptionsProvider = default!;
    private IConnectionMultiplexerFactory _connectionMultiplexerFactory = default!;
    private readonly string _connectionString = "localhost:6379";

    [Fact]
    public void NotNullConnection()
    {
        var connector = new RedisConnector(_telemetryProvider, _redisConfigurationOptionsProvider, _connectionMultiplexerFactory, _redisOptions);
        connector.Database.Should().NotBeNull();
        connector.Subscriber.Should().NotBeNull();
        connector.Dispose();
    }


    [Fact]
    public void ConnectionStringExtraParams()
    {
        var opt = new RedisConnectionOptions
        {
            ConnectionString = "localhost:6379,ssl=True,abortConnect=True,connectTimeout=1001",
            ConnectionStringExtraParams = "allowAdmin=true,abortConnect=false,connectRetry=2,keepAlive=30,name=test,syncTimeout=250,connectTimeout=1000"
        };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));
        var connection = sut.GetConfiguration();
        connection.AllowAdmin.Should().BeTrue();
        connection.AbortOnConnectFail.Should().BeFalse();
        connection.ConnectRetry.Should().Be(2);
        connection.KeepAlive.Should().Be(30);
        connection.ClientName.Should().Be("test");
        connection.SyncTimeout.Should().Be(250);
        connection.ConnectTimeout.Should().Be(1000);
    }

    [Theory]
    [InlineData("localhost:6379,ssl=True,abortConnect=True,password=abc",
        "allowAdmin=true,abortConnect=false,connectRetry=2,keepAlive=30,name=test,syncTimeout=250,connectTimeout=1000",
        "localhost:6379,name=test,keepAlive=30,syncTimeout=250,allowAdmin=True,version=6.0,connectTimeout=1000,password=abc,ssl=True,abortConnect=False,connectRetry=2")]
    [InlineData("", "allowAdmin=true", "")]
    public void ConnectionStringExtraParamsX(string connectionString, string extraParams, string expected)
    {
        var opt = new RedisConnectionOptions
        {
            ConnectionString = connectionString,
            ConnectionStringExtraParams = extraParams
        };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));
        var cnn = sut.GetConfiguration().ToString();
        cnn.Should().Be(expected);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _telemetryProvider = _fixture.Create<ICachingTelemetryProvider>();
        _profiler = _fixture.Create<IRedisProfiler>();
        _redisOptions = Options.Create(new RedisConnectionOptions
        {
            ConnectionString = _connectionString
        });
        _fixture.Inject(_redisOptions);
        _redisConfigurationOptionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, _redisOptions);
        _fixture.Inject(_redisConfigurationOptionsProvider);
        _connectionMultiplexerFactory = new ConnectionMultiplexerFactory(_redisOptions, _profiler);
        _fixture.Inject(_connectionMultiplexerFactory);
        return Task.CompletedTask;
    }
}
