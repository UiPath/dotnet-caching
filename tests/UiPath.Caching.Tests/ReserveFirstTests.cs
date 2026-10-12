using System.Reflection;
using UiPath.Caching.Locking;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>A miss takes its key in the run table before any lock: callers join the run at once, however long it takes, and TryAdd takes the same key.</summary>
public class ReserveFirstTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Callers_join_a_run_for_longer_than_the_local_lock_timeout()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalSingleFlightTimeout = TimeSpan.FromMilliseconds(20) });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<string?> Slow(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return "slow";
        }

        var first = cache.GetOrAddAsync<string>("k", Slow, policy: null, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var joiners = Enumerable.Range(0, 7).Select(_ => cache.GetOrAddAsync<string>("k", Slow, policy: null, Ct).AsTask()).ToArray();
        await Task.Delay(200, Ct);
        joiners.Should().OnlyContain(j => !j.IsCompleted);
        release.SetResult();

        (await Task.WhenAll(joiners.Append(first))).Should().AllBe("slow");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_failing_run_fails_every_caller_that_joined_it()
    {
        using var cache = InMemoryMultilayer.Cache();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<string?> Failing(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("boom");
        }

        var first = cache.GetOrAddAsync<string>("k", Failing, policy: null, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var joiners = Enumerable.Range(0, 7).Select(_ => cache.GetOrAddAsync<string>("k", Failing, policy: null, Ct).AsTask()).ToArray();
        release.SetResult();

        foreach (var caller in joiners.Append(first))
        {
            await caller.Invoking(c => c).Should().ThrowAsync<InvalidOperationException>();
        }

        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_run_whose_starter_cancels_still_stores_the_value_for_the_callers_that_joined()
    {
        var inner = MissingInner();
        var stored = 0;
        ValueTaskStubs.Returns(
            inner.SetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
            ci =>
            {
                ci.ArgAt<CancellationToken>(4).ThrowIfCancellationRequested();
                Interlocked.Increment(ref stored);
                return new ValueTask<bool>(true);
            });
        using var cache = InMemoryMultilayer.Cache(inner: inner);
        using var leaving = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Generate(CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            return "generated";
        }

        var starter = cache.GetOrAddAsync<string>("k", Generate, policy: null, leaving.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var joiner = cache.GetOrAddAsync<string>("k", Generate, policy: null, Ct).AsTask();
        await leaving.CancelAsync();
        await starter.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();

        (await joiner.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().Be("generated");
        stored.Should().Be(1, "the joiner still waits for the run, so its value is stored for the next caller");
    }

    [Fact]
    public async Task A_cancelled_caller_gets_its_cancellation_through_the_task_as_it_does_for_an_async_method()
    {
        using var cache = InMemoryMultilayer.Cache();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        ValueTask<string?> call = default;

        var start = () => call = cache.GetOrAddAsync<string>("k", Generated, policy: null, cancelled.Token);

        start.Should().NotThrow("a cancelled token is reported through the task");
        await call.AsTask().Invoking(t => t).Should().ThrowExactlyAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_missing_key_is_reported_through_the_task_too()
    {
        using var cache = InMemoryMultilayer.Cache();
        ValueTask<string?> call = default;

        var start = () => call = cache.GetOrAddAsync<string>(default, Generated, policy: null, Ct);

        start.Should().NotThrow();
        await call.AsTask().Invoking(t => t).Should().ThrowExactlyAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task A_caller_whose_inner_read_straddles_a_run_the_local_tier_did_not_keep_generates_after_it_never_beside_it()
    {
        // The inner read is the one place a late caller can still see an older state; with nothing kept locally, only a new run answers it.
        var inner = Substitute.For<ICache>();
        var reads = 0;
        var lateRead = new TaskCompletionSource<ICacheEntry<string?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTaskStubs.Returns(
            inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
            _ =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    return new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue });
                }

                lateReadStarted.TrySetResult();
                return new ValueTask<ICacheEntry<string?>>(lateRead.Task);
            });
        ValueTaskStubs.Returns(inner.SetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()), true);
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalMaxExpiration = TimeSpan.FromMilliseconds(1), LocalMaxExpirationDisconnected = TimeSpan.FromMilliseconds(1) }, inner);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var overlapped = false;
        var calls = 0;
        async Task<string?> Generate(CancellationToken _)
        {
            if (Interlocked.Increment(ref running) > 1)
            {
                overlapped = true;
            }

            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref running);
            return "generated";
        }

        var first = cache.GetOrAddAsync<string>("k", Generate, policy: null, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var late = cache.GetOrAddAsync<string>("k", Generate, policy: null, Ct).AsTask();
        await lateReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        release.SetResult();
        (await first).Should().Be("generated");
        await Task.Delay(50, Ct);
        lateRead.SetResult(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue });

        (await late).Should().Be("generated");
        calls.Should().Be(2, "nothing was kept for the late caller to find, so it generates once more");
        overlapped.Should().BeFalse("after the first run, never beside it");
    }

    [Fact]
    public async Task A_run_that_failed_leaves_the_key_free_for_the_next_caller()
    {
        using var cache = InMemoryMultilayer.Cache();
        var generations = Generations(cache);

        await cache.Invoking(c => c.GetOrAddAsync<string>("k", _ => throw new InvalidOperationException("boom"), policy: null, Ct).AsTask()).Should().ThrowAsync<InvalidOperationException>();

        generations.Count.Should().Be(0);
        (await cache.GetOrAddAsync<string>("k", Generated, policy: null, Ct)).Should().Be("generated");
    }

    [Fact]
    public async Task TryAdd_while_a_run_generates_the_key_loses_after_the_wait_and_the_generated_value_stands()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalSingleFlightTimeout = TimeSpan.FromMilliseconds(50) });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Slow(CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            return "generated";
        }

        var run = cache.GetOrAddAsync<string>("k", Slow, policy: null, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        (await cache.TryAddAsync<string>("k", "added", policy: null, Ct)).Should().BeFalse();
        release.SetResult();

        (await run).Should().Be("generated");
        (await cache.GetAsync<string>("k", policy: null, Ct)).Should().Be("generated");
    }

    [Fact]
    public async Task TryAdd_waits_for_a_run_that_ends_within_the_local_lock_timeout_and_then_finds_the_key()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalSingleFlightTimeout = TimeSpan.FromSeconds(30) });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Slow(CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            return "generated";
        }

        var run = cache.GetOrAddAsync<string>("k", Slow, policy: null, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var add = cache.TryAddAsync<string>("k", "added", policy: null, Ct).AsTask();
        await Task.Delay(100, Ct);
        add.IsCompleted.Should().BeFalse("it waits for the run, as it waited for the lock the run held");
        release.SetResult();

        (await add).Should().BeFalse();
        (await run).Should().Be("generated");
        (await cache.GetAsync<string>("k", policy: null, Ct)).Should().Be("generated");
    }

    [Fact]
    public async Task TryAdd_wins_when_a_run_that_stored_nothing_ends_while_it_waits()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalSingleFlightTimeout = TimeSpan.FromSeconds(30) });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Nothing(CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            return null;
        }

        var run = cache.GetOrAddAsync<string>("k", Nothing, policy: null, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var add = cache.TryAddAsync<string>("k", "added", policy: null, Ct).AsTask();
        await Task.Delay(100, Ct);
        release.SetResult();

        (await add).Should().BeTrue();
        (await run).Should().BeNull();
        (await cache.GetAsync<string>("k", policy: null, Ct)).Should().Be("added");
    }

    [Fact]
    public async Task A_miss_that_arrives_during_a_TryAdd_joins_it_and_takes_its_value()
    {
        var inner = MissingInner();
        var innerAdd = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerAddCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTaskStubs.Returns(
            inner.TryAddAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
            _ =>
            {
                innerAddCalled.TrySetResult();
                return new ValueTask<bool>(innerAdd.Task);
            });
        using var cache = InMemoryMultilayer.Cache(inner: inner);
        var generated = 0;
        Task<string?> Generate(CancellationToken _)
        {
            Interlocked.Increment(ref generated);
            return Task.FromResult<string?>("generated");
        }

        var add = cache.TryAddAsync<string>("k", "added", policy: null, Ct).AsTask();
        await innerAddCalled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var miss = cache.GetOrAddAsync<string>("k", Generate, policy: null, Ct).AsTask();
        await Task.Delay(100, Ct);
        miss.IsCompleted.Should().BeFalse("the key is taken by the TryAdd");
        innerAdd.SetResult(true);

        (await add).Should().BeTrue();
        (await miss).Should().Be("added");
        generated.Should().Be(0);
    }

    [Fact]
    public async Task A_miss_that_joined_a_TryAdd_which_lost_generates_the_key_itself()
    {
        var inner = MissingInner();
        var innerAdd = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerAddCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTaskStubs.Returns(
            inner.TryAddAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
            _ =>
            {
                innerAddCalled.TrySetResult();
                return new ValueTask<bool>(innerAdd.Task);
            });
        using var cache = InMemoryMultilayer.Cache(inner: inner);
        var generations = Generations(cache);
        var generated = 0;
        Task<string?> Generate(CancellationToken _)
        {
            Interlocked.Increment(ref generated);
            return Task.FromResult<string?>("generated");
        }

        var add = cache.TryAddAsync<string>("k", "added", policy: null, Ct).AsTask();
        await innerAddCalled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var miss = cache.GetOrAddAsync<string>("k", Generate, policy: null, Ct).AsTask();
        await Task.Delay(100, Ct);
        innerAdd.SetResult(false);

        (await add).Should().BeFalse();
        (await miss).Should().Be("generated");
        generated.Should().Be(1);
        generations.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_TryAdd_whose_inner_add_throws_loses_and_releases_the_key()
    {
        var inner = MissingInner();
        ValueTaskStubs.Returns(
            inner.TryAddAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
            _ => throw new InvalidOperationException("inner tier down"));
        using var cache = InMemoryMultilayer.Cache(inner: inner);
        var generations = Generations(cache);

        (await cache.TryAddAsync<string>("k", "added", policy: null, Ct)).Should().BeFalse();

        generations.Count.Should().Be(0);
        (await cache.GetOrAddAsync<string>("k", Generated, policy: null, Ct)).Should().Be("generated");
    }

    [Fact]
    public async Task A_TryAdd_cancelled_while_it_holds_the_key_releases_it()
    {
        var inner = MissingInner();
        var innerAddCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTaskStubs.Returns(
            inner.TryAddAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
            call =>
            {
                innerAddCalled.TrySetResult();
                return new ValueTask<bool>(UntilCancelled(call.Arg<CancellationToken>()));
            });
        using var cache = InMemoryMultilayer.Cache(inner: inner);
        var generations = Generations(cache);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var add = cache.TryAddAsync<string>("k", "added", policy: null, cancellation.Token).AsTask();
        await innerAddCalled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cancellation.CancelAsync();

        await add.Invoking(a => a).Should().ThrowAsync<OperationCanceledException>();
        generations.Count.Should().Be(0);
        (await cache.GetOrAddAsync<string>("k", Generated, policy: null, Ct)).Should().Be("generated");
    }

    [Fact]
    public async Task A_starter_that_cancels_while_its_run_waits_for_the_distributed_lock_does_not_fail_the_callers_that_joined()
    {
        var policy = new CachePolicy { Lock = new LockProfile { DistributedLockEnabled = true } };
        var gate = new GatedDistributedLock();
        using var cache = InMemoryMultilayer.Cache(inner: MissingInner(), distributedLock: gate);
        using var leaving = new CancellationTokenSource();

        var starter = cache.GetOrAddAsync<string>("k", Generated, policy, leaving.Token).AsTask();
        await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var joiner = cache.GetOrAddAsync<string>("k", Generated, policy, Ct).AsTask();
        await leaving.CancelAsync();
        await starter.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        gate.Release.SetResult();

        (await joiner.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().Be("generated", "the read under the lock runs for the run, not for the caller that left");
    }

    private static async Task<bool> UntilCancelled(CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        return true;
    }

    private static Task<string?> Generated(CancellationToken token) => Task.FromResult<string?>("generated");

    private static Task<KeyValuePair<string, string?>[]> GeneratedBatch(string[] states, CancellationToken token) =>
        Task.FromResult(states.Select(s => new KeyValuePair<string, string?>(s, "generated")).ToArray());

    private static Task<IDictionary<string, string?>> GeneratedHash(CancellationToken token) =>
        Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["f"] = "generated" });

    private static ICache MissingInner()
    {
        var inner = Substitute.For<ICache>();
        ValueTaskStubs.Returns(
            inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
            new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue }));
        ValueTaskStubs.Returns(inner.SetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()), true);
        return inner;
    }

    private static InFlight<InFlightKey, object?> Generations(MultilayerCache cache) =>
        (InFlight<InFlightKey, object?>)typeof(MultilayerCache).GetField("_generations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;

    /// <summary>A distributed lock granted only once the test releases it.</summary>
    private sealed class GatedDistributedLock : IDistributedLock
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IAsyncDisposable> AcquireAsync(string key, TimeSpan expiry, TimeSpan waitTimeout, CancellationToken token)
        {
            Waiting.TrySetResult();
            await Release.Task.WaitAsync(token);
            return await NullDistributedLock.Instance.AcquireAsync(key, expiry, waitTimeout, token);
        }
    }
}
