using NSubstitute;
using StackExchange.Redis;
using UiPath.Caching.Redis;
using UiPath.Caching.Tests.Fakes;
using Xunit;

namespace UiPath.Caching.Tests.Redis;

public sealed class ConnectingPipelineTests
{
    [Fact]
    public void A_connected_command_runs_in_the_callers_frame()
    {
        // Not a substitute: its call would allocate on its own.
        using var redis = new ConnectedRedis();
        var sut = new ConnectingPipeline(redis, new PassThroughPipeline());
        var pending = new TaskCompletionSource<int>().Task;
        static ValueTask<int> Command(Task<int> task, CancellationToken _) => new ValueTask<int>(task);
#pragma warning disable CA2012 // Warm-up: the command stays pending on purpose.
        _ = sut.ExecuteAsync(Command, pending, 0, TestContext.Current.CancellationToken);
#pragma warning restore CA2012

        // An async frame around a command still waiting for its reply would box a state machine.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = sut.ExecuteAsync(Command, pending, 0, TestContext.Current.CancellationToken);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0);
        result.IsCompleted.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_command_waits_for_a_pending_connect(bool withState)
    {
        var connected = new TaskCompletionSource();
        var redis = Substitute.For<IRedisConnector>();
#pragma warning disable CA2012 // NSubstitute stores the ValueTask to hand it back.
        redis.ConnectAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask(connected.Task));
#pragma warning restore CA2012
        var sut = new ConnectingPipeline(redis, new RecordingResiliencePipeline());
        var ran = 0;

        var result = Execute(sut, withState, () => new ValueTask<int>(Interlocked.Increment(ref ran)));

        ran.Should().Be(0);
        connected.SetResult();
        (await result).Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_connect_fails_the_command_without_running_it(bool withState)
    {
        var redis = Substitute.For<IRedisConnector>();
#pragma warning disable CA2012 // NSubstitute stores the ValueTask to hand it back.
        redis.ConnectAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromException(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "down")));
#pragma warning restore CA2012
        var sut = new ConnectingPipeline(redis, new RecordingResiliencePipeline());
        var ran = false;

        var act = async () => await Execute(sut, withState, () =>
        {
            ran = true;
            return new ValueTask<int>(1);
        });

        await act.Should().ThrowAsync<RedisConnectionException>();
        ran.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_command_that_throws_before_it_returns_fails_its_task(bool withState)
    {
        using var redis = new ConnectedRedis();
        var sut = new ConnectingPipeline(redis, new PassThroughPipeline());

        var canceled = Execute(sut, withState, static () => throw new OperationCanceledException());
        var faulted = Execute(sut, withState, static () => throw new InvalidOperationException());

        canceled.IsCanceled.Should().BeTrue();
        faulted.IsFaulted.Should().BeTrue();
        await FluentActions.Awaiting(async () => await faulted).Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_connect_that_throws_synchronously_fails_the_task_without_running_the_command(bool withState)
    {
        // A connector outside the library may throw instead of returning a faulted task; the pipeline then calls the callback directly.
        var redis = Substitute.For<IRedisConnector>();
#pragma warning disable CA2012 // NSubstitute stores the ValueTask to hand it back.
        redis.ConnectAsync(Arg.Any<CancellationToken>()).Returns<ValueTask>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "down"));
#pragma warning restore CA2012
        var sut = new ConnectingPipeline(redis, new PassThroughPipeline());
        var ran = false;

        var result = Execute(sut, withState, () =>
        {
            ran = true;
            return new ValueTask<int>(1);
        });

        result.IsFaulted.Should().BeTrue();
        await FluentActions.Awaiting(async () => await result).Should().ThrowAsync<RedisConnectionException>();
        ran.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_completed_connect_is_consumed_once(bool withState)
    {
        // A connector may hand out a pooled source; one never consumed never goes back to its pool.
        var source = new CompletedSource();
        var redis = Substitute.For<IRedisConnector>();
#pragma warning disable CA2012 // NSubstitute stores the ValueTask to hand it back.
        redis.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask(source, 0));
#pragma warning restore CA2012
        var sut = new ConnectingPipeline(redis, new PassThroughPipeline());

        (await Execute(sut, withState, static () => new ValueTask<int>(1))).Should().Be(1);

        source.Consumed.Should().Be(1);
    }

    // Both overloads: the one that takes state, and the one whose command closes over what it needs.
    private static ValueTask<int> Execute(ConnectingPipeline sut, bool withState, Func<ValueTask<int>> command) =>
        withState
            ? sut.ExecuteAsync(static (c, _) => c(), command, 0, TestContext.Current.CancellationToken)
            : sut.ExecuteAsync(_ => command(), 0, TestContext.Current.CancellationToken);

    private sealed class PassThroughPipeline : UiPath.Caching.Policies.IResiliencePipeline
    {
        public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default) =>
            callback(cancellationToken);

        public ValueTask<TResult> ExecuteAsync<TResult, TState>(Func<TState, CancellationToken, ValueTask<TResult>> callback, TState state, TResult defaultValue, CancellationToken cancellationToken = default) =>
            callback(state, cancellationToken);
    }

    // Like a pooled source: once consumed, its token is stale and any further look at the ValueTask throws.
    private sealed class CompletedSource : System.Threading.Tasks.Sources.IValueTaskSource
    {
        public int Consumed { get; private set; }

        public void GetResult(short token)
        {
            ThrowIfStale(token);
            Consumed++;
        }

        public System.Threading.Tasks.Sources.ValueTaskSourceStatus GetStatus(short token)
        {
            ThrowIfStale(token);
            return System.Threading.Tasks.Sources.ValueTaskSourceStatus.Succeeded;
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token, System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags) =>
            continuation(state);

        private void ThrowIfStale(short token)
        {
            if (Consumed > 0 || token != 0)
            {
                throw new InvalidOperationException("The ValueTask was used after it was consumed.");
            }
        }
    }

    private sealed class ConnectedRedis : IRedisConnector
    {
        public event EventHandler? OnConnectionFailed { add { } remove { } }

        public event EventHandler? OnConnectionRestored { add { } remove { } }

        public event EventHandler? OnReconnected { add { } remove { } }

        public bool IsConnected => true;

        public Version Version { get; } = new(7, 4);

        public IDatabase Database => throw new NotSupportedException();

        public ISubscriber Subscriber => throw new NotSupportedException();

        public void ForceReconnect()
        {
        }

        public System.Net.EndPoint[] GetEndPoints(bool configuredOnly = false) => [];

        public void Dispose()
        {
        }
    }
}
