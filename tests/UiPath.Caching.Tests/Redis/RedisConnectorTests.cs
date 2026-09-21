using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Redis;

public class RedisConnectorTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private readonly string _connectionString = "localhost:6379";
    private ICachingTelemetryProvider _telemetryProvider = default!;
    private IOptions<RedisConnectionOptions> _redisOptions = default!;
    private IRedisConfigurationOptionsProvider _redisConfigurationOptionsProvider = default!;
    private IConnectionMultiplexerFactory _connectionMultiplexerFactory = default!;

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
            ConnectionStringExtraParams = "allowAdmin=true,abortConnect=false,connectRetry=2,keepAlive=30,name=test,syncTimeout=250,connectTimeout=1000",
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
            ConnectionStringExtraParams = extraParams,
        };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));
        var cnn = sut.GetConfiguration().ToString();
        cnn.Should().Be(expected);
    }

#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
    [Theory]
    [InlineData(RedisMaintenanceNotifications.Auto, MaintenanceNotificationMode.Auto)]
    [InlineData(RedisMaintenanceNotifications.Required, MaintenanceNotificationMode.Enabled)]
    [InlineData(RedisMaintenanceNotifications.Disabled, MaintenanceNotificationMode.Disabled)]
    public void MaintenanceNotifications_MapsOntoTheClientMode(RedisMaintenanceNotifications configured, MaintenanceNotificationMode expected)
    {
        // Required maps to Enabled, not a same-named member, so a reversed arm would silently do nothing.
        var opt = new RedisConnectionOptions
        {
            ConnectionString = "localhost:6379",
            MaintenanceNotifications = configured,
        };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));

        sut.GetConfiguration().MaintenanceNotifications.Should().Be(expected);
    }

    [Fact]
    public void MaintenanceNotifications_AppliesWithoutAConnectionString()
    {
        // With no connection string these options are what a supplied ConnectionFactory is handed.
        var opt = new RedisConnectionOptions { MaintenanceNotifications = RedisMaintenanceNotifications.Auto };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));

        sut.GetConfiguration().MaintenanceNotifications.Should().Be(MaintenanceNotificationMode.Auto);
    }

    [Fact]
    public void MaintenanceNotifications_RejectsAnUnsupportedValue()
    {
        // Configuration binds enums from numbers, and folding an unknown one into Disabled would be
        // indistinguishable from asking and being refused.
        var opt = new RedisConnectionOptions
        {
            ConnectionString = "localhost:6379",
            MaintenanceNotifications = (RedisMaintenanceNotifications)3,
        };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));

        // Invalid configuration rather than a bad argument: the value reaches this from the options, not
        // from a parameter of the method that rejects it.
        sut.Invoking(p => p.GetConfiguration()).Should().Throw<InvalidOperationException>()
            .WithMessage("*RedisMaintenanceNotifications*");
    }

    [Fact]
    public void MaintenanceNotifications_LeavesTheClientDefault_WhenUnset()
    {
        var opt = new RedisConnectionOptions { ConnectionString = "localhost:6379" };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));

        sut.GetConfiguration().MaintenanceNotifications
            .Should().Be(new ConfigurationOptions().MaintenanceNotifications, "null must not overwrite what the client itself decides");
    }
#pragma warning restore SER010

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _telemetryProvider = _fixture.Create<ICachingTelemetryProvider>();
        _redisOptions = Options.Create(new RedisConnectionOptions
        {
            ConnectionString = _connectionString,
        });
        _fixture.Inject(_redisOptions);
        _redisConfigurationOptionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, _redisOptions);
        _fixture.Inject(_redisConfigurationOptionsProvider);
        _connectionMultiplexerFactory = new SubstituteMultiplexerFactory();
        _fixture.Inject(_connectionMultiplexerFactory);
        return ValueTask.CompletedTask;
    }

    private sealed class SubstituteMultiplexerFactory : IConnectionMultiplexerFactory
    {
        public ValueTask<IConnectionMultiplexer> CreateAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default) =>
            new(Substitute.For<IConnectionMultiplexer>());
    }
}
