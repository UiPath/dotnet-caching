using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class SingleFlightTests : RouterTestBase
{
    [Fact]
    public async Task Concurrent_misses_for_one_key_share_one_load_and_one_write()
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryRuns = 0;

        async Task<Item?> Factory(CancellationToken token)
        {
            Interlocked.Increment(ref factoryRuns);
            entered.TrySetResult();
            await gate.Task;
            return new Item("made");
        }

        var first = node.Cache.GetOrAddAsync("k", Factory, default, Ct).AsTask();
        await entered.Task;
        var others = Enumerable.Range(0, 9).Select(_ => node.Cache.GetOrAddAsync("k", Factory, default, Ct).AsTask()).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(others.Append(first));

        results.Should().OnlyContain(item => item == new Item("made"));
        factoryRuns.Should().Be(1);
        node.LibraryBackend.Reads.Should().Be(1);
        node.LibraryBackend.Writes.Should().Be(1);
    }

    [Fact]
    public async Task The_path_captured_by_the_first_caller_carries_into_the_write_and_serves_the_waiters()
    {
        var library = true;
        var node = NewNode(useLibrary: _ => library, localCap: TimeSpan.Zero);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Item?> Factory(CancellationToken token)
        {
            entered.TrySetResult();
            await gate.Task;
            return new Item("made");
        }

        var first = node.Cache.GetOrAddAsync("k", Factory, default, Ct).AsTask();
        await entered.Task;
        library = false;
        var waiter = node.Cache.GetOrAddAsync("k", Factory, default, Ct).AsTask();
        gate.SetResult();

        (await waiter).Should().Be(new Item("made"));
        await first;
        node.LibraryBackend.Writes.Should().Be(1, "the write belongs to the path the load started on");
        node.SourceBackend.Writes.Should().Be(0);
        node.SourceBackend.Reads.Should().Be(0, "the waiter did not read for itself");
    }

    [Fact]
    public async Task A_cancelled_first_caller_does_not_fail_a_waiter_that_is_not_cancelled()
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Item?> Slow(CancellationToken token)
        {
            entered.TrySetResult();
            await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token);
            return null;
        }

        var first = node.Cache.GetOrAddAsync("k", Slow, default, cts.Token).AsTask();
        await entered.Task;
        var waiter = node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("second")), default, Ct).AsTask();
        await cts.CancelAsync();

        await first.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        (await waiter).Should().Be(new Item("second"));
    }

    [Fact]
    public async Task A_factory_failure_reaches_every_waiter_and_the_next_call_loads_again()
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Item?> Failing(CancellationToken token)
        {
            entered.TrySetResult();
            await gate.Task;
            throw new InvalidOperationException("origin down");
        }

        var first = node.Cache.GetOrAddAsync("k", Failing, default, Ct).AsTask();
        await entered.Task;
        var waiter = node.Cache.GetOrAddAsync("k", Failing, default, Ct).AsTask();
        gate.SetResult();

        await first.Invoking(task => task).Should().ThrowAsync<InvalidOperationException>();
        await waiter.Invoking(task => task).Should().ThrowAsync<InvalidOperationException>();
        (await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("again")), default, Ct)).Should().Be(new Item("again"));
    }
}
