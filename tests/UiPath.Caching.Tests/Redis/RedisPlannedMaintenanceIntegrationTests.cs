using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class RedisPlannedMaintenanceIntegrationTests(RedisContainerFixture fixture)
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Enqueue(formatter(state, exception));
            }
        }
    }

    private sealed class ThrowingConnectionMultiplexerFactory(int expectedAttempts) : IConnectionMultiplexerFactory
    {
        private readonly TaskCompletionSource _expectedAttemptsReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _createCount;

        public int CreateCount => Volatile.Read(ref _createCount);

        public Task ExpectedAttemptsReached => _expectedAttemptsReached.Task;

        public ValueTask<IConnectionMultiplexer> CreateAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
        {
            var createCount = Interlocked.Increment(ref _createCount);
            if (createCount >= expectedAttempts)
            {
                _expectedAttemptsReached.TrySetResult();
            }

            return new ValueTask<IConnectionMultiplexer>(
                Task.FromException<IConnectionMultiplexer>(
                    new RedisConnectionException(ConnectionFailureType.UnableToConnect, "boom")));
        }
    }

    private static RedisPlannedMaintenance NewMaintenance(
        RedisConnectionOptions connectionOptions,
        CapturingLogger<RedisPlannedMaintenance> logger,
        IConnectionMultiplexerFactory? factory = null)
    {
        var options = Options.Create(connectionOptions);
        var optionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options);
        factory ??= new ConnectionMultiplexerFactory(options, NullRedisProfiler.Instance);
        return new RedisPlannedMaintenance(NullTelemetryProvider.Instance, Substitute.For<IRedisConnector>(), optionsProvider, factory, logger, options);
    }

    private static async Task WaitForWarningsAsync(CapturingLogger<RedisPlannedMaintenance> logger, int count, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 200 && logger.Warnings.Count < count; i++)
        {
            await Task.Delay(50, cancellationToken);
        }
    }

    [Fact]
    public async Task StartAsync_SubscribesWithoutWarnings_AgainstLiveRedis()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");

        var logger = new CapturingLogger<RedisPlannedMaintenance>();
        using var maintenance = NewMaintenance(
            new RedisConnectionOptions { ConnectionString = fixture.ConnectionString, EnableHangDetection = false },
            logger);

        await maintenance.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        logger.Warnings.Should().BeEmpty();

        await maintenance.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_RetriesAndWarns_WhenConnectionFails()
    {
        var logger = new CapturingLogger<RedisPlannedMaintenance>();
        using var maintenance = NewMaintenance(
            new RedisConnectionOptions
            {
                ConnectionString = "127.0.0.1:6399,connectTimeout=200,connectRetry=1",
                AbortOnConnectFail = true,
                EnableHangDetection = false,
                PlannedMaintenanceConnectionRetryCount = 2,
                PlannedMaintenanceConnectionRetryDelay = TimeSpan.FromMilliseconds(50),
            },
            logger);

        await maintenance.StartAsync(TestContext.Current.CancellationToken);
        await WaitForWarningsAsync(logger, 2, TestContext.Current.CancellationToken);

        logger.Warnings.Count.Should().BeGreaterThanOrEqualTo(2);

        await maintenance.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_GivesUpAfterConfiguredRetryCount_WhenConnectionFails()
    {
        var logger = new CapturingLogger<RedisPlannedMaintenance>();
        var factory = new ThrowingConnectionMultiplexerFactory(expectedAttempts: 2);
        using var maintenance = NewMaintenance(
            new RedisConnectionOptions
            {
                ConnectionString = "localhost:6379",
                EnableHangDetection = false,
                PlannedMaintenanceConnectionRetryCount = 2,
                PlannedMaintenanceConnectionRetryDelay = TimeSpan.FromMilliseconds(10),
            },
            logger,
            factory);

        await maintenance.StartAsync(TestContext.Current.CancellationToken);
        await factory.ExpectedAttemptsReached.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await WaitForWarningsAsync(logger, 2, TestContext.Current.CancellationToken);
        var createCountAfterConfiguredAttempts = factory.CreateCount;
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        createCountAfterConfiguredAttempts.Should().Be(2);
        factory.CreateCount.Should().Be(createCountAfterConfiguredAttempts);
        logger.Warnings.Should().HaveCount(2);
        logger.Warnings.Should().Contain(message => message.Contains("giving up", StringComparison.Ordinal));

        await maintenance.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_ClampsNegativeRetryDelay_WhenConnectionFails()
    {
        var logger = new CapturingLogger<RedisPlannedMaintenance>();
        using var maintenance = NewMaintenance(
            new RedisConnectionOptions
            {
                ConnectionString = "127.0.0.1:6399,connectTimeout=200,connectRetry=1",
                AbortOnConnectFail = true,
                EnableHangDetection = false,
                PlannedMaintenanceConnectionRetryCount = 2,
                PlannedMaintenanceConnectionRetryDelay = TimeSpan.FromSeconds(-5),
            },
            logger);

        await maintenance.StartAsync(TestContext.Current.CancellationToken);
        await WaitForWarningsAsync(logger, 2, TestContext.Current.CancellationToken);

        logger.Warnings.Count.Should().BeGreaterThanOrEqualTo(2);

        await maintenance.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopAsync_HaltsRetryLoop_WhenConnectionFails()
    {
        var logger = new CapturingLogger<RedisPlannedMaintenance>();
        using var maintenance = NewMaintenance(
            new RedisConnectionOptions
            {
                ConnectionString = "127.0.0.1:6399,connectTimeout=200,connectRetry=1",
                AbortOnConnectFail = true,
                EnableHangDetection = false,
                PlannedMaintenanceConnectionRetryCount = 100,
                PlannedMaintenanceConnectionRetryDelay = TimeSpan.FromMilliseconds(100),
            },
            logger);

        await maintenance.StartAsync(TestContext.Current.CancellationToken);
        await WaitForWarningsAsync(logger, 2, TestContext.Current.CancellationToken);

        await maintenance.StopAsync(TestContext.Current.CancellationToken);
        var countAtStop = logger.Warnings.Count;

        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        (logger.Warnings.Count - countAtStop).Should().BeLessThanOrEqualTo(2);
    }
}
