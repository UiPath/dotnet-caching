using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
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

    private sealed class FakeConnector(Func<ValueTask>? onConnect = null) : IRedisConnector
    {
        private int _connectCount;
        public int ConnectCount => Volatile.Read(ref _connectCount);

        public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            if (onConnect is not null)
            {
                await onConnect().ConfigureAwait(false);
            }
        }

        public bool IsConnected => false;
        public Version Version => new(6, 0);
        public IDatabase Database => throw new NotSupportedException();
        public ISubscriber Subscriber => throw new NotSupportedException();
        public EndPoint[] GetEndPoints(bool configuredOnly = false) => [];
        public void ForceReconnect() { }
        public void Dispose() { }
        public event EventHandler? OnConnectionFailed { add { } remove { } }
        public event EventHandler? OnConnectionRestored { add { } remove { } }
        public event EventHandler? OnReconnected { add { } remove { } }
    }

    [Fact]
    public async Task StartAsync_TriggersConnect()
    {
        var invoked = new TaskCompletionSource();
        var connector = new FakeConnector(() =>
        {
            invoked.TrySetResult();
            return ValueTask.CompletedTask;
        });
        var sut = new RedisConnectionWarmup(connector, NullTelemetryProvider.Instance);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        connector.ConnectCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_TracksException_WhenConnectFails()
    {
        var connector = new FakeConnector(() => throw new InvalidOperationException("boom"));
        var telemetry = new CapturingTelemetry();
        using var sut = new RedisConnectionWarmup(connector, telemetry);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await telemetry.ExceptionTracked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        connector.ConnectCount.Should().Be(1);
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutThrowing()
    {
        var connector = new FakeConnector();
        using var sut = new RedisConnectionWarmup(connector, NullTelemetryProvider.Instance);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        var stop = async () => await sut.StopAsync(TestContext.Current.CancellationToken);

        await stop.Should().NotThrowAsync();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var sut = new RedisConnectionWarmup(new FakeConnector(), NullTelemetryProvider.Instance);

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
