using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Redis;

public class RedisConnectionWarmupTests
{
    private sealed class CapturingTelemetry : ICachingTelemetryProvider
    {
        private readonly TaskCompletionSource _exceptionTracked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ExceptionTracked => _exceptionTracked.Task;
        public void TrackException(Exception ex, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default) => _exceptionTracked.TrySetResult();
    }

    [Fact]
    public async Task StartAsync_TriggersConnect()
    {
        var connector = Substitute.For<IRedisConnector>();
        var invoked = new TaskCompletionSource();
        connector.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            invoked.TrySetResult();
            return ValueTask.CompletedTask;
        });
        var sut = new RedisConnectionWarmup(connector, NullTelemetryProvider.Instance);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await connector.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_TracksException_WhenConnectFails()
    {
        var connector = Substitute.For<IRedisConnector>();
        connector.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("boom"))));
        var telemetry = new CapturingTelemetry();
        using var sut = new RedisConnectionWarmup(connector, telemetry);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await telemetry.ExceptionTracked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await connector.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutThrowing()
    {
        var connector = Substitute.For<IRedisConnector>();
        connector.ConnectAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        using var sut = new RedisConnectionWarmup(connector, NullTelemetryProvider.Instance);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        var stop = async () => await sut.StopAsync(TestContext.Current.CancellationToken);

        await stop.Should().NotThrowAsync();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var connector = Substitute.For<IRedisConnector>();
        var sut = new RedisConnectionWarmup(connector, NullTelemetryProvider.Instance);

        var dispose = () =>
        {
            sut.Dispose();
            sut.Dispose();
        };

        dispose.Should().NotThrow();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddRedisConnection_RegistersWarmup_PerWarmUpOnStart(bool warmUpOnStart)
    {
        var services = new ServiceCollection();
        var builder = Substitute.For<ICachingBuilder>();
        builder.Services.Returns(services);
        builder.Enabled.Returns(true);

        builder.AddRedisConnection(o => o.WarmUpOnStart = warmUpOnStart);

        services.Any(d => d.ImplementationType == typeof(RedisConnectionWarmup)).Should().Be(warmUpOnStart);
    }
}
