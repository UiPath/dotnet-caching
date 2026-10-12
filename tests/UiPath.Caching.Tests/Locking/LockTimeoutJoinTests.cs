using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Locking;

/// <summary>A caller whose wait for the local lock times out joins the generator already running for the key; it runs its own only when none is.</summary>
public class LockTimeoutJoinTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PastTheTimeout = TimeSpan.FromMilliseconds(400);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InMemoryCacheOptions Options => new() { LocalLockTimeout = LockTimeout };

    [Fact]
    public async Task A_waiter_that_times_out_waits_for_the_running_generator_instead_of_running_its_own()
    {
        using var cache = InMemoryMultilayer.Cache(Options);
        var holder = new Slow("holder");
        var otherCalls = 0;

        var held = Run(cache, "k", holder.Generate, Ct);
        await holder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var waiter = Run(cache, "k", _ => { Interlocked.Increment(ref otherCalls); return Task.FromResult<string?>("waiter"); }, Ct);
        await Task.Delay(PastTheTimeout, Ct);

        waiter.IsCompleted.Should().BeFalse("the lock wait has timed out, and the load in flight is the one to wait for");
        holder.Release.SetResult();

        (await held).Should().Be("holder");
        (await waiter).Should().Be("holder");
        otherCalls.Should().Be(0);
        holder.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_slow_load_is_not_stampeded_by_the_callers_that_outwait_the_lock()
    {
        using var cache = InMemoryMultilayer.Cache(Options);
        var load = new Slow("loaded");

        var reads = Enumerable.Range(0, 25).Select(_ => Run(cache, "k", load.Generate, Ct)).ToArray();
        await load.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(PastTheTimeout, Ct);
        load.Release.SetResult();

        (await Task.WhenAll(reads)).Should().AllBe("loaded");
        load.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_waiter_that_cancels_stops_waiting_and_leaves_the_load_to_the_others()
    {
        using var cache = InMemoryMultilayer.Cache(Options);
        var holder = new Slow("holder");
        using var waiterCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var held = Run(cache, "k", holder.Generate, Ct);
        await holder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var waiter = Run(cache, "k", holder.Generate, waiterCancellation.Token);
        await Task.Delay(PastTheTimeout, Ct);
        await waiterCancellation.CancelAsync();

        await ((Func<Task>)(() => waiter)).Should().ThrowAsync<OperationCanceledException>();
        holder.Cancelled.Should().BeFalse("the caller that still waits keeps the shared load alive");
        holder.Release.SetResult();
        (await held).Should().Be("holder");
    }

    [Fact]
    public async Task The_load_is_cancelled_once_every_caller_has_cancelled()
    {
        using var cache = InMemoryMultilayer.Cache(Options);
        var holder = new Slow("holder");
        using var holderCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var waiterCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var held = Run(cache, "k", holder.Generate, holderCancellation.Token);
        await holder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var waiter = Run(cache, "k", holder.Generate, waiterCancellation.Token);
        await Task.Delay(PastTheTimeout, Ct);
        await holderCancellation.CancelAsync();
        await waiterCancellation.CancelAsync();

        await ((Func<Task>)(() => held)).Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => waiter)).Should().ThrowAsync<OperationCanceledException>();
        await holder.CancelledSignal.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task A_holder_that_cancels_leaves_the_load_to_the_waiter_that_joined()
    {
        using var cache = InMemoryMultilayer.Cache(Options);
        var holder = new Slow("holder");
        using var holderCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var held = Run(cache, "k", holder.Generate, holderCancellation.Token);
        await holder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var waiter = Run(cache, "k", holder.Generate, Ct);
        await Task.Delay(PastTheTimeout, Ct);
        await holderCancellation.CancelAsync();

        await ((Func<Task>)(() => held)).Should().ThrowAsync<OperationCanceledException>();
        holder.Cancelled.Should().BeFalse();
        holder.Release.SetResult();
        (await waiter).Should().Be("holder");
        holder.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_load_fails_the_callers_that_joined_it_and_the_next_call_starts_a_new_one()
    {
        using var cache = InMemoryMultilayer.Cache(Options);
        var failing = new Slow("never");

        var held = Run(cache, "k", failing.Generate, Ct);
        await failing.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var waiter = Run(cache, "k", failing.Generate, Ct);
        await Task.Delay(PastTheTimeout, Ct);
        failing.Fail.SetResult();

        await ((Func<Task>)(() => held)).Should().ThrowAsync<InvalidOperationException>();
        await ((Func<Task>)(() => waiter)).Should().ThrowAsync<InvalidOperationException>();
        (await Run(cache, "k", _ => Task.FromResult<string?>("again"), Ct)).Should().Be("again");
    }

    [Fact]
    public async Task A_generator_that_supplies_its_expiration_is_joined_too()
    {
        using var cache = InMemoryMultilayer.Cache(Options);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<GeneratedValue<string>> Generate(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return new GeneratedValue<string>("jwt", DateTimeOffset.UtcNow.AddMinutes(5));
        }

        var held = cache.GetOrAddWithExpirationAsync<string>("k", Generate, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var waiter = cache.GetOrAddWithExpirationAsync<string>("k", Generate, Ct).AsTask();
        await Task.Delay(PastTheTimeout, Ct);
        release.SetResult();

        (await held).Should().Be("jwt");
        (await waiter).Should().Be("jwt");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_hash_waiter_that_times_out_waits_for_the_running_generator_too()
    {
        using var cache = InMemoryMultilayer.HashCache(Options);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<IDictionary<string, string?>> Generate(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return new Dictionary<string, string?> { ["f"] = "v" };
        }

        var held = Task.Run(async () => await cache.GetOrAddAsync<string>("k", Generate, policy: null, Ct), Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var waiter = Task.Run(async () => await cache.GetOrAddAsync<string>("k", Generate, policy: null, Ct), Ct);
        await Task.Delay(PastTheTimeout, Ct);
        waiter.IsCompleted.Should().BeFalse();
        release.SetResult();

        (await held)["f"].Should().Be("v");
        (await waiter)["f"].Should().Be("v");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Without_the_local_lock_callers_still_run_their_own_generators()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalLockEnabled = false });
        var holder = new Slow("holder");

        var first = Run(cache, "k", holder.Generate, Ct);
        await holder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = Run(cache, "k", _ => Task.FromResult<string?>("second"), Ct);

        (await second).Should().Be("second");
        holder.Release.SetResult();
        (await first).Should().Be("holder");
    }

    private static Task<string?> Run(MultilayerCache cache, string key, Func<CancellationToken, Task<string?>> generator, CancellationToken token) =>
        Task.Run(async () => await cache.GetOrAddAsync(key, generator, (CachePolicy?)null, token), Ct);

    private sealed class Slow(string value)
    {
        private int _calls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancelledSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public bool Cancelled => CancelledSignal.Task.IsCompleted;

        public async Task<string?> Generate(CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            using var registration = token.Register(() => CancelledSignal.TrySetResult());
            var finished = await Task.WhenAny(Release.Task, Fail.Task, CancelledSignal.Task).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (finished == Fail.Task)
            {
                throw new InvalidOperationException("the load failed");
            }

            return value;
        }
    }
}
