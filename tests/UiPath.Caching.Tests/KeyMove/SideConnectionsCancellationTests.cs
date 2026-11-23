using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>A connect that finishes after its wait was cancelled has no owner, so its multiplexer is disposed.</summary>
public sealed class SideConnectionsCancellationTests
{
    [Fact]
    public async Task A_multiplexer_that_connects_after_the_wait_was_cancelled_is_disposed()
    {
        var connect = new TaskCompletionSource<IConnectionMultiplexer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = InterceptingProxy.Wrap<IConnectionMultiplexer>(null!, (method, _) =>
        {
            if (method.Name == nameof(IConnectionMultiplexer.DisposeAsync))
            {
                disposed.TrySetResult();
                return ValueTask.CompletedTask;
            }

            throw new NotSupportedException(method.Name);
        });
        using var cancel = new CancellationTokenSource();
        var waiting = SideConnections.AwaitAsync(connect.Task, cancel.Token);

        await cancel.CancelAsync();
        await waiting.Invoking(w => w).Should().ThrowAsync<OperationCanceledException>();
        disposed.Task.IsCompleted.Should().BeFalse("it has not connected yet");
        connect.SetResult(late);

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_multiplexer_that_connects_in_time_is_returned_and_not_disposed()
    {
        var disposed = false;
        var mux = InterceptingProxy.Wrap<IConnectionMultiplexer>(null!, (method, _) =>
        {
            if (method.Name == nameof(IConnectionMultiplexer.DisposeAsync))
            {
                disposed = true;
                return ValueTask.CompletedTask;
            }

            throw new NotSupportedException(method.Name);
        });

        var result = await SideConnections.AwaitAsync(Task.FromResult(mux), TestContext.Current.CancellationToken);

        result.Should().BeSameAs(mux);
        disposed.Should().BeFalse();
    }

    [Fact]
    public async Task A_connect_that_fails_after_the_wait_was_cancelled_is_swallowed()
    {
        var connect = new TaskCompletionSource<IConnectionMultiplexer>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var waiting = SideConnections.AwaitAsync(connect.Task, cancel.Token);

        await cancel.CancelAsync();
        await waiting.Invoking(w => w).Should().ThrowAsync<OperationCanceledException>();
        connect.SetException(new InvalidOperationException("late failure"));

        await Task.Delay(50, TestContext.Current.CancellationToken);
    }
}
