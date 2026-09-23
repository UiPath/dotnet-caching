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

    // On the rendered string: the option travels as whole seconds.
    [Theory]
    [InlineData("localhost:6379,asyncTimeout=3000", "maintRelaxedTimeout=6")]
    [InlineData("localhost:6379,syncTimeout=3000", "maintRelaxedTimeout=6")]
    // 500ms renders as zero without rounding up.
    [InlineData("localhost:6379,asyncTimeout=250", "maintRelaxedTimeout=1")]
    [InlineData("localhost:6379,asyncTimeout=400000", "maintRelaxedTimeout=600")]
    public void MaintenanceRelaxedTimeout_IsDerived_FromTheAsyncTimeout(string connectionString, string expected)
    {
        var opt = new RedisConnectionOptions { ConnectionString = connectionString };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));
        var config = sut.GetConfiguration();

        sut.ReapplyDerivedBounds(config);

        config.ToString().Should().Contain(expected);
    }

    [Fact]
    public void MaintenanceRelaxedTimeout_TakesTheConfiguredValue_OverTheDerivedOne()
    {
        var opt = new RedisConnectionOptions
        {
            ConnectionString = "localhost:6379,asyncTimeout=3000",
            MaintenanceRelaxedTimeout = TimeSpan.FromSeconds(42),
        };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));

        sut.GetConfiguration().ToString().Should().Contain("maintRelaxedTimeout=42");
    }

    [Theory]
    [InlineData(0.5, "maintRelaxedWindowMax=1")]
    [InlineData(45d, "maintRelaxedWindowMax=45")]
    [InlineData(900d, "maintRelaxedWindowMax=600")]
    public void MaintenanceRelaxedWindowMax_IsAppliedInWholeSeconds(double configuredSeconds, string expected)
    {
        var opt = new RedisConnectionOptions
        {
            ConnectionString = "localhost:6379,asyncTimeout=3000",
            MaintenanceRelaxedWindowMax = TimeSpan.FromSeconds(configuredSeconds),
        };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));

        sut.GetConfiguration().ToString().Should().Contain(expected);
    }

    [Fact]
    public void MaintenanceRelaxedWindowMax_IsLeftToTheClient_WhenNotConfigured()
    {
        var opt = new RedisConnectionOptions { ConnectionString = "localhost:6379,asyncTimeout=3000" };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));

        sut.GetConfiguration().ToString().Should().NotContain("maintRelaxedWindowMax");
    }

    [Fact]
    public void MaintenanceRelaxedTimeout_RoundsUpAConfiguredSubSecondValue()
    {
        var opt = new RedisConnectionOptions
        {
            ConnectionString = "localhost:6379,asyncTimeout=3000",
            MaintenanceRelaxedTimeout = TimeSpan.FromMilliseconds(500),
        };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));

        sut.GetConfiguration().ToString().Should().Contain("maintRelaxedTimeout=1");
    }

    [Fact]
    public void MaintenanceRelaxedTimeout_IsNotDerived_WithoutAConnectionString()
    {
        var opt = new RedisConnectionOptions { ConnectionString = string.Empty };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));

        sut.GetConfiguration().ToString().Should().NotContain("maintRelaxedTimeout");
    }

    [Fact]
    public void MaintenanceRelaxedTimeout_IsStillApplied_WithoutAConnectionString_WhenConfigured()
    {
        var opt = new RedisConnectionOptions
        {
            ConnectionString = string.Empty,
            MaintenanceRelaxedTimeout = TimeSpan.FromSeconds(42),
        };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));

        sut.GetConfiguration().ToString().Should().Contain("maintRelaxedTimeout=42");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void FailFastBacklogPolicy_DefaultsToFailingFast(bool? configured, bool expectFailFast)
    {
        // Assigned even when null, or the initializer would be tested instead.
        var opt = new RedisConnectionOptions { ConnectionString = "localhost:6379", FailFastBacklogPolicy = configured };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));

        var policy = sut.GetConfiguration().BacklogPolicy;

        policy.Should().BeSameAs(expectFailFast ? BacklogPolicy.FailFast : BacklogPolicy.Default);
    }

    [Fact]
    public void AnExplicitMaintRelaxedTimeout_InTheConnectionString_IsNotDerivedOver()
    {
        var opt = new RedisConnectionOptions { ConnectionString = "localhost:6379,asyncTimeout=5000,maintRelaxedTimeout=42" };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));

        sut.GetConfiguration().ToString().Should().Contain("maintRelaxedTimeout=42");

        // Equal to the client default, so only presence can tell it was set.
        var sameAsDefault = new RedisConnectionOptions { ConnectionString = "localhost:6379,asyncTimeout=1000,maintRelaxedTimeout=10" };
        var provider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(sameAsDefault));

        provider.GetConfiguration().ToString().Should().Contain("maintRelaxedTimeout=10");
    }

    [Fact]
    public void TheRelaxedTimeout_IsDerivedFromTheAsyncTimeout_AConfiguratorLeftBehind()
    {
        var opt = new RedisConnectionOptions { ConnectionString = "localhost:6379,asyncTimeout=1000" };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));
        var config = sut.GetConfiguration();

        config.AsyncTimeout = 30000;
        sut.ReapplyDerivedBounds(config);

        config.ToString().Should().Contain("maintRelaxedTimeout=60", "the bound follows the timeout the connection ends up with");
    }

    [Fact]
    public void ReapplyDerivedBounds_HandlesAConnectionBuiltWithoutAConnectionString()
    {
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(new RedisConnectionOptions { ConnectionString = null! }));
        var config = sut.GetConfiguration();

        var act = () => sut.ReapplyDerivedBounds(config);

        act.Should().NotThrow();
        config.ToString().Should().NotContain("maintRelaxedTimeout", "there is no configured timeout to derive from");
    }

    [Fact]
    public void ReapplyDerivedBounds_FollowsASyncTimeoutAConfiguratorSetWithoutAConnectionString()
    {
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(new RedisConnectionOptions { ConnectionString = null! }));
        var config = sut.GetConfiguration();

        config.SyncTimeout = 4000;
        sut.ReapplyDerivedBounds(config);

        config.ToString().Should().Contain("maintRelaxedTimeout=8");
    }

    [Fact]
    public void ReapplyDerivedBounds_LeavesASuppliedRelaxedTimeoutAlone()
    {
        var opt = new RedisConnectionOptions { ConnectionString = "localhost:6379,asyncTimeout=1000,maintRelaxedTimeout=42" };
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(opt));
        var config = sut.GetConfiguration();

        config.AsyncTimeout = 30000;
        sut.ReapplyDerivedBounds(config);

        config.ToString().Should().Contain("maintRelaxedTimeout=42", "it was supplied, not derived");
    }

    [Fact]
    public void FailFastBacklogPolicy_ReachesAConnectionBuiltWithoutAConnectionString()
    {
        var sut = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(new RedisConnectionOptions()));

        var policy = sut.GetConfiguration().BacklogPolicy;

        policy.Should().BeSameAs(BacklogPolicy.FailFast);
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
