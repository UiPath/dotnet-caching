using UiPath.Caching.Locking;

namespace UiPath.Caching.Tests.Locking;

public class AsyncKeyedLocalLockTests(ITestContextAccessor testContextAccessor)
{
    private static AsyncKeyedLocalLock NewLocker() =>
        new(Options.Create(new CacheOptions()));

    [Fact]
    public async Task Acquire_returns_disposable_that_releases_on_dispose()
    {
        using var sut = NewLocker();
        var token = testContextAccessor.Current.CancellationToken;

        var lease = await sut.AcquireAsync("k1", token);
        lease.Should().NotBeNull();
        lease.Dispose();

        var second = await sut.AcquireAsync("k1", token);
        second.Should().NotBeNull();
        second.Dispose();
    }

    [Fact]
    public async Task Different_keys_do_not_block_each_other()
    {
        using var sut = NewLocker();
        var token = testContextAccessor.Current.CancellationToken;

        var leaseA = await sut.AcquireAsync("a", token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var leaseB = await sut.AcquireAsync("b", cts.Token);
        leaseB.Should().NotBeNull();
        leaseB.Dispose();
        leaseA.Dispose();
    }

    [Fact]
    public async Task Concurrent_callers_on_same_key_run_serially()
    {
        using var sut = NewLocker();
        var token = testContextAccessor.Current.CancellationToken;
        var inCriticalSection = 0;
        var maxObserved = 0;
        var callCount = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Worker()
        {
            using var lease = await sut.AcquireAsync("hot-key", token);
            var current = Interlocked.Increment(ref inCriticalSection);
            int observedSnapshot;
            do
            {
                observedSnapshot = Volatile.Read(ref maxObserved);
                if (current <= observedSnapshot) break;
            }
            while (Interlocked.CompareExchange(ref maxObserved, current, observedSnapshot) != observedSnapshot);

            if (firstEntered.TrySetResult())
            {
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            }
            Interlocked.Increment(ref callCount);
            Interlocked.Decrement(ref inCriticalSection);
        }

        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(Worker)).ToArray();

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        release.TrySetResult();

        await Task.WhenAll(tasks);

        maxObserved.Should().Be(1, "the local locker must serialize holders of the same key");
        callCount.Should().Be(32, "all workers should run to completion");
    }

    [Fact]
    public async Task Cancellation_token_cancels_pending_wait()
    {
        using var sut = NewLocker();
        var token = testContextAccessor.Current.CancellationToken;

        var holder = await sut.AcquireAsync("blocked", token);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Func<Task> act = async () => await sut.AcquireAsync("blocked", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        holder.Dispose();
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_leak_the_slot_to_subsequent_callers()
    {
        using var sut = NewLocker();
        var token = testContextAccessor.Current.CancellationToken;

        var holder = await sut.AcquireAsync("hot", token);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Func<Task> cancelled = async () => await sut.AcquireAsync("hot", cts.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        holder.Dispose();

        using var afterReleaseCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var second = await sut.AcquireAsync("hot", afterReleaseCts.Token);
        second.Should().NotBeNull("the cancelled waiter must not have left the semaphore held");
        second.Dispose();
    }

    [Fact]
    public void Ctor_throws_when_LocalLockPoolSize_is_zero_or_negative()
    {
        var opts = Options.Create(new CacheOptions { LocalLockPoolSize = 0 });
        var act = () => new AsyncKeyedLocalLock(opts);
        var ex = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        ex.ParamName.Should().Be("cacheOptions.LocalLockPoolSize");
        ex.Message.Should().Contain(nameof(CacheOptions.LocalLockPoolSize));
    }

    [Fact]
    public void Ctor_throws_when_LocalLockPoolInitialFill_is_negative()
    {
        var opts = Options.Create(new CacheOptions
        {
            LocalLockPoolSize = 10,
            LocalLockPoolInitialFill = -1,
        });
        var act = () => new AsyncKeyedLocalLock(opts);
        var ex = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        ex.ParamName.Should().Be("cacheOptions.LocalLockPoolInitialFill");
        ex.Message.Should().Contain(nameof(CacheOptions.LocalLockPoolInitialFill));
    }

    [Fact]
    public void Ctor_throws_when_LocalLockPoolInitialFill_exceeds_LocalLockPoolSize()
    {
        var opts = Options.Create(new CacheOptions
        {
            LocalLockPoolSize = 5,
            LocalLockPoolInitialFill = 6,
        });
        var act = () => new AsyncKeyedLocalLock(opts);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("cacheOptions.LocalLockPoolInitialFill");
    }

    [Fact]
    public async Task Acquire_after_cancellation_still_blocks_while_holder_is_alive()
    {
        using var sut = NewLocker();
        var token = testContextAccessor.Current.CancellationToken;

        var holder = await sut.AcquireAsync("hot", token);

        using var firstCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Func<Task> firstWaiter = async () => await sut.AcquireAsync("hot", firstCts.Token);
        await firstWaiter.Should().ThrowAsync<OperationCanceledException>();

        // Holder is still alive — a subsequent waiter must still block until its own cancellation fires.
        // If the cancelled waiter had leaked or signaled the underlying slot, this would either succeed
        // prematurely (slot freed by cancellation, not by Dispose) or hang past its own deadline.
        using var secondCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Func<Task> secondWaiter = async () => await sut.AcquireAsync("hot", secondCts.Token);
        await secondWaiter.Should().ThrowAsync<OperationCanceledException>();

        holder.Dispose();
    }
}
