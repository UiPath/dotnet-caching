using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests;

/// <summary>Cleanup steps that must end, or leave a task a bound covers, whatever the background work and the connection do.</summary>
public sealed class RouterCleanupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_drain_ends_on_a_completed_task_whose_removal_has_not_run()
    {
        var core = new RouterCore(TimeProvider.System, _ => true, NullLogger.Instance);
        core.AddWithoutRemoval(Task.CompletedTask);
        core.AddWithoutRemoval(Task.FromException(new InvalidOperationException("ended badly")));

        // On the pool: a drain that spins without yielding would otherwise hold this thread.
        var drain = Task.Run(core.DrainAsync, Ct);

        (await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(5), Ct))).Should().BeSameAs(drain, "completed tasks are removed by the drain itself");
        core.IsIdle.Should().BeTrue();
    }

    [Fact]
    public async Task The_drain_waits_for_a_running_task()
    {
        var core = new RouterCore(TimeProvider.System, _ => true, NullLogger.Instance);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.AddWithoutRemoval(running.Task);

        var drain = Task.Run(core.DrainAsync, Ct);
        await Task.Delay(100, Ct);
        drain.IsCompleted.Should().BeFalse();

        running.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task A_topic_release_on_a_connection_that_never_opens_gives_up_with_the_token()
    {
        var source = new Uri($"urn:release-{Guid.NewGuid():N}");
        var connector = new NeverConnects();
        using var bound = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var release = RouterServiceCollectionExtensions.ReleaseTopicAsync(new FakeBus().For(source), connector, "s", "g", new TopicKey("t"), bound.Token);

        var act = () => release.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await act.Should().ThrowAsync<OperationCanceledException>();
        connector.DatabaseRead.Should().BeFalse("the database is read only after the connection is open");
    }

    private sealed class NeverConnects : IRedisConnector
    {
        public event EventHandler? OnConnectionFailed
        {
            add { }
            remove { }
        }

        public event EventHandler? OnConnectionRestored
        {
            add { }
            remove { }
        }

        public event EventHandler? OnReconnected
        {
            add { }
            remove { }
        }

        public bool DatabaseRead { get; private set; }

        public bool IsConnected => false;

        public Version Version => new(7, 4);

        public IDatabase Database
        {
            get
            {
                DatabaseRead = true;
                throw new InvalidOperationException("A synchronous connect would block here.");
            }
        }

        public ISubscriber Subscriber => throw new InvalidOperationException("Not used.");

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => new(Task.Delay(Timeout.Infinite, cancellationToken));

        public void ForceReconnect()
        {
        }

        public EndPoint[] GetEndPoints(bool configuredOnly = false) => [];

        public void Dispose()
        {
        }
    }
}
