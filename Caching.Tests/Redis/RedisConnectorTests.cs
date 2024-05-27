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


    [Fact]
    public void ConnectionStringExtraParams()
    {
        var opt = new RedisConnectionOptions
        {
            ConnectionString = "localhost:6379,ssl=True,abortConnect=True,connectTimeout=1001",
            ConnectionStringExtraParams = "allowAdmin=true,abortConnect=false,connectRetry=2,keepAlive=30,name=test,syncTimeout=250,connectTimeout=1000"
        };

        var connection = opt.CreateConfigurationOptions();
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

        var cnn = opt.CreateConfigurationOptions().ToString();
        cnn.Should().Be(expected);
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
