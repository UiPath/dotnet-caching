using System.Reflection;
using UiPath.Caching.Locking;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

public class SharedGenerationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_batch_cancelled_while_it_reserves_its_keys_leaves_none_reserved()
    {
        using var cache = InMemoryMultilayer.Cache();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var calls = 0;
        var generations = ReplaceGenerations(cache, new CallbackComparer(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                cancellation.Cancel();
            }
        }));

        var pending = cache.GetOrAddAsync<string, string>(
            [new("a", "a"), new("b", "b")],
            (states, _) => Task.FromResult(states.Select(s => new KeyValuePair<string, string?>(s, s)).ToArray()),
            policy: null,
            cancellation.Token).AsTask();

        await Throws<OperationCanceledException>(pending);
        generations.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_batch_whose_generator_fails_leaves_none_reserved_and_the_next_caller_runs()
    {
        using var cache = InMemoryMultilayer.Cache();
        var generations = ReplaceGenerations(cache, null);

        var failing = cache.GetOrAddAsync<string, string>([new("a", "a")], (_, _) => throw new InvalidOperationException("boom"), policy: null, Ct).AsTask();
        await Throws<InvalidOperationException>(failing);
        generations.Count.Should().Be(0);

        var again = await cache.GetOrAddAsync<string, string>([new("a", "a")], (states, _) => Task.FromResult(states.Select(s => new KeyValuePair<string, string?>(s, "ok")).ToArray()), policy: null, Ct);
        again.Single().Value.Should().Be("ok");
    }

    [Fact]
    public async Task A_batch_and_a_single_key_call_for_one_key_run_one_generator()
    {
        using var cache = InMemoryMultilayer.Cache();
        using var batchAtReservation = new ManualResetEventSlim();
        using var allowReservation = new ManualResetEventSlim();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var singleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batchGenerating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batchGeneratedKey = 0;
        var hashCalls = 0;
        ReplaceGenerations(cache, new CallbackComparer(() =>
        {
            if (Interlocked.Increment(ref hashCalls) == 1)
            {
                batchAtReservation.Set();
                allowReservation.Wait(TimeSpan.FromSeconds(10), Ct).Should().BeTrue();
            }
        }));

        async Task<KeyValuePair<string, string?>[]> GenerateBatch(string[] states, CancellationToken token)
        {
            if (states.Contains("k"))
            {
                Interlocked.Increment(ref batchGeneratedKey);
            }

            batchGenerating.TrySetResult();
            await release.Task.WaitAsync(token);
            return states.Select(s => new KeyValuePair<string, string?>(s, "batch")).ToArray();
        }

        async Task<string?> GenerateSingle(CancellationToken token)
        {
            singleStarted.TrySetResult();
            await release.Task.WaitAsync(token);
            return "single";
        }

        var batch = Task.Run(async () => await cache.GetOrAddAsync<string, string>([new("k", "k"), new("other", "other")], GenerateBatch, policy: null, Ct), Ct);
        batchAtReservation.Wait(TimeSpan.FromSeconds(10), Ct).Should().BeTrue();
        var single = cache.GetOrAddAsync<string>("k", GenerateSingle, policy: null, Ct).AsTask();
        await singleStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        allowReservation.Set();

        // The batch has taken or joined every key once its generator runs; releasing before that would let the single run end first and free "k".
        await batchGenerating.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        release.SetResult();

        var results = await batch;
        (await single).Should().Be("single");
        results.Single(p => p.Key == "k").Value.Should().Be("single");
        batchGeneratedKey.Should().Be(0);
    }

    [Fact]
    public async Task Joiners_of_a_cancelled_batch_regenerate_its_key_once()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalLockTimeout = TimeSpan.FromMilliseconds(5) });
        using var starterCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starter = cache.GetOrAddAsync<string, string>(
            [new("k", "k")],
            async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return [];
            },
            policy: null,
            starterCancellation.Token).AsTask();
        await started.Task;

        var joinedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joinedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kCalls = 0;
        async Task<KeyValuePair<string, string?>[]> Generate(string[] states, CancellationToken token)
        {
            if (states.Contains("a"))
            {
                joinedA.TrySetResult();
            }

            if (states.Contains("b"))
            {
                joinedB.TrySetResult();
            }

            if (states.Contains("k"))
            {
                Interlocked.Increment(ref kCalls);
                await release.Task.WaitAsync(token);
            }

            return states.Select(s => new KeyValuePair<string, string?>(s, s)).ToArray();
        }

        var a = cache.GetOrAddAsync<string, string>([new("k", "k"), new("a", "a")], Generate, policy: null, Ct).AsTask();
        var b = cache.GetOrAddAsync<string, string>([new("k", "k"), new("b", "b")], Generate, policy: null, Ct).AsTask();
        await Task.WhenAll(joinedA.Task, joinedB.Task).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await starterCancellation.CancelAsync();
        await Throws<OperationCanceledException>(starter);
        await Task.Delay(100, Ct);
        release.SetResult();
        await Task.WhenAll(a, b);

        kCalls.Should().Be(1);
        (await a).Single(p => p.Key == "k").Value.Should().Be("k");
        (await b).Single(p => p.Key == "k").Value.Should().Be("k");
    }

    [Fact]
    public async Task A_cancelled_starter_keeps_the_distributed_lease_until_the_shared_load_ends()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalLockTimeout = TimeSpan.FromMilliseconds(5) });
        var distributed = Substitute.For<IDistributedLock>();
        var lease = new RecordingLease();
        distributed.AcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAsyncDisposable>(lease), new ValueTask<IAsyncDisposable>(NoOpAsyncDisposable.Instance));
        typeof(MultilayerCacheBase).GetField("_distributedLock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cache, distributed);
        var policy = new CachePolicy { Lock = new LockProfile { DistributedLockEnabled = true, LocalLockTimeout = TimeSpan.FromMilliseconds(5) } };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedToken = CancellationToken.None;
        async Task<string?> Load(CancellationToken token)
        {
            sharedToken = token;
            started.SetResult();
            await release.Task.WaitAsync(token);
            return "loaded";
        }

        var starter = cache.GetOrAddAsync<string>("k", Load, policy, cancellation.Token).AsTask();
        await started.Task;
        var joiner = cache.GetOrAddAsync<string>("k", _ => throw new InvalidOperationException("should join"), policy, Ct).AsTask();
        await Task.Delay(200, Ct);

        await cancellation.CancelAsync();
        await Throws<OperationCanceledException>(starter);
        var releasedWhileRunning = lease.Disposed.Task.IsCompleted;
        release.SetResult();

        (await joiner).Should().Be("loaded");
        sharedToken.IsCancellationRequested.Should().BeFalse();
        releasedWhileRunning.Should().BeFalse();
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task A_single_key_caller_that_reserves_after_the_key_was_written_does_not_generate()
    {
        using var cache = InMemoryMultilayer.Cache();
        using var atReservation = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        ReplaceGenerations(cache, PauseOnce(atReservation, proceed));
        var generated = 0;
        Task<string?> Generate(CancellationToken token)
        {
            Interlocked.Increment(ref generated);
            return Task.FromResult<string?>("generated");
        }

        var call = Task.Run(async () => await cache.GetOrAddAsync<string>("k", Generate, policy: null, Ct), Ct);
        atReservation.Wait(TimeSpan.FromSeconds(10), Ct).Should().BeTrue();
        (await cache.SetAsync<string>("k", "written", Ct)).Should().BeTrue();
        proceed.Set();

        (await call).Should().Be("written");
        generated.Should().Be(0);
    }

    [Fact]
    public async Task A_batch_that_reserves_after_one_key_was_written_generates_only_the_other()
    {
        using var cache = InMemoryMultilayer.Cache();
        using var atReservation = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        ReplaceGenerations(cache, PauseOnce(atReservation, proceed));
        var generatedStates = new List<string>();
        Task<KeyValuePair<string, string?>[]> Generate(string[] states, CancellationToken token)
        {
            lock (generatedStates)
            {
                generatedStates.AddRange(states);
            }

            return Task.FromResult(states.Select(s => new KeyValuePair<string, string?>(s, "generated")).ToArray());
        }

        var call = Task.Run(async () => await cache.GetOrAddAsync<string, string>([new("k1", "k1"), new("k2", "k2")], Generate, policy: null, Ct), Ct);
        atReservation.Wait(TimeSpan.FromSeconds(10), Ct).Should().BeTrue();
        (await cache.SetAsync<string>("k1", "written", Ct)).Should().BeTrue();
        proceed.Set();

        var results = await call;
        results.Single(p => p.Key == "k1").Value.Should().Be("written");
        results.Single(p => p.Key == "k2").Value.Should().Be("generated");
        generatedStates.Should().Equal("k2");
    }

    [Fact]
    public async Task A_batch_that_reserves_after_every_key_was_written_does_not_generate()
    {
        using var cache = InMemoryMultilayer.Cache();
        using var atReservation = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var generations = ReplaceGenerations(cache, PauseOnce(atReservation, proceed));
        var generated = 0;
        Task<KeyValuePair<string, string?>[]> Generate(string[] states, CancellationToken token)
        {
            Interlocked.Increment(ref generated);
            return Task.FromResult(states.Select(s => new KeyValuePair<string, string?>(s, "generated")).ToArray());
        }

        var call = Task.Run(async () => await cache.GetOrAddAsync<string, string>([new("k1", "k1"), new("k2", "k2")], Generate, policy: null, Ct), Ct);
        atReservation.Wait(TimeSpan.FromSeconds(10), Ct).Should().BeTrue();
        await cache.SetAsync<string>("k1", "w1", Ct);
        await cache.SetAsync<string>("k2", "w2", Ct);
        proceed.Set();

        (await call).Select(p => p.Value).Should().Equal("w1", "w2");
        generated.Should().Be(0);
        generations.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_hash_cache_caller_that_reserves_after_the_key_was_written_does_not_generate()
    {
        using var cache = InMemoryMultilayer.HashCache();
        using var atReservation = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        typeof(MultilayerHashCache).GetField("_generations", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(cache, new InFlight<InFlightKey, object?>(PauseOnce(atReservation, proceed)));
        var generated = 0;
        Task<IDictionary<string, string?>> Generate(CancellationToken token)
        {
            Interlocked.Increment(ref generated);
            return Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["f"] = "generated" });
        }

        var call = Task.Run(async () => await cache.GetOrAddAsync<string>("k", Generate, policy: null, Ct), Ct);
        atReservation.Wait(TimeSpan.FromSeconds(10), Ct).Should().BeTrue();
        await cache.SetAsync<string>("k", new Dictionary<string, string?> { ["f"] = "written" }, Ct);
        proceed.Set();

        (await call)["f"].Should().Be("written");
        generated.Should().Be(0);
    }

    [Fact]
    public async Task The_recheck_after_taking_a_key_does_not_read_the_inner_tier()
    {
        var inner = MissingInner();
        using var cache = InMemoryMultilayer.Cache(inner: inner);
        using var atReservation = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        ReplaceGenerations(cache, PauseOnce(atReservation, proceed));
        var readsAtGenerator = -1;
        Task<string?> Generate(CancellationToken token)
        {
            readsAtGenerator = InnerReads(inner);
            return Task.FromResult<string?>("generated");
        }

        var call = Task.Run(async () => await cache.GetOrAddAsync<string>("k", Generate, policy: null, Ct), Ct);
        atReservation.Wait(TimeSpan.FromSeconds(10), Ct).Should().BeTrue();
        var readsBefore = InnerReads(inner);
        proceed.Set();

        (await call).Should().Be("generated");
        readsBefore.Should().BeGreaterThan(0);
        readsAtGenerator.Should().Be(readsBefore);
    }

    [Fact]
    public async Task The_batch_recheck_after_taking_keys_does_not_read_the_inner_tier()
    {
        var inner = MissingInner();
        using var cache = InMemoryMultilayer.Cache(inner: inner);
        using var atReservation = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        ReplaceGenerations(cache, PauseOnce(atReservation, proceed));
        var readsAtGenerator = -1;
        Task<KeyValuePair<string, string?>[]> Generate(string[] states, CancellationToken token)
        {
            readsAtGenerator = InnerReads(inner);
            return Task.FromResult(states.Select(s => new KeyValuePair<string, string?>(s, "generated")).ToArray());
        }

        var call = Task.Run(async () => await cache.GetOrAddAsync<string, string>([new("k1", "k1"), new("k2", "k2")], Generate, policy: null, Ct), Ct);
        atReservation.Wait(TimeSpan.FromSeconds(10), Ct).Should().BeTrue();
        var readsBefore = InnerReads(inner);
        proceed.Set();

        (await call).Should().HaveCount(2);
        readsBefore.Should().BeGreaterThan(0);
        readsAtGenerator.Should().Be(readsBefore);
    }

    [Fact]
    public async Task A_run_writes_the_local_tier_before_it_leaves_the_table_single_key()
    {
        using var cache = InMemoryMultilayer.Cache();
        var generated = false;
        var seenAtExit = new List<string?>();
        ReplaceGenerations(cache, new CallbackComparer(() =>
        {
            if (Volatile.Read(ref generated))
            {
                seenAtExit.Add(cache.GetAsync<string>("k", Ct).AsTask().GetAwaiter().GetResult());
            }
        }));

        Task<string?> Generate(CancellationToken token)
        {
            Volatile.Write(ref generated, true);
            return Task.FromResult<string?>("generated");
        }

        await cache.GetOrAddAsync<string>("k", Generate, policy: null, Ct);

        seenAtExit.Should().NotBeEmpty().And.AllBe("generated");
    }

    [Fact]
    public async Task A_run_writes_the_local_tier_before_it_leaves_the_table_batch()
    {
        using var cache = InMemoryMultilayer.Cache();
        var generated = false;
        var seenAtExit = new List<string?>();
        ReplaceGenerations(cache, new CallbackComparer(() =>
        {
            if (Volatile.Read(ref generated))
            {
                seenAtExit.Add(cache.GetAsync<string>("k", Ct).AsTask().GetAwaiter().GetResult());
            }
        }));

        Task<KeyValuePair<string, string?>[]> Generate(string[] states, CancellationToken token)
        {
            Volatile.Write(ref generated, true);
            return Task.FromResult(states.Select(s => new KeyValuePair<string, string?>(s, "generated")).ToArray());
        }

        await cache.GetOrAddAsync<string, string>([new("k", "k")], Generate, policy: null, Ct);

        seenAtExit.Should().NotBeEmpty().And.AllBe("generated");
    }

    [Fact]
    public async Task An_abandoned_run_does_not_store_over_a_newer_value()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalLockTimeout = TimeSpan.FromMilliseconds(5) });
        var generations = ReplaceGenerations(cache, null);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Old(CancellationToken token)
        {
            started.SetResult();
            await release.Task;
            return "old";
        }

        var old = cache.GetOrAddAsync<string>("k", Old, policy: null, cancellation.Token).AsTask();
        await started.Task;
        var oldRun = generations.Completions().Single();
        await cancellation.CancelAsync();
        await Throws<OperationCanceledException>(old);

        (await cache.GetOrAddAsync<string>("k", _ => Task.FromResult<string?>("new"), policy: null, Ct)).Should().Be("new");
        release.SetResult();
        await oldRun.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        (await cache.GetAsync<string>("k", Ct)).Should().Be("new");
    }

    [Fact]
    public async Task An_abandoned_hash_run_does_not_store_over_a_newer_value()
    {
        using var cache = InMemoryMultilayer.HashCache(new InMemoryCacheOptions { LocalLockTimeout = TimeSpan.FromMilliseconds(5) });
        var generations = new InFlight<InFlightKey, object?>();
        typeof(MultilayerHashCache).GetField("_generations", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cache, generations);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IDictionary<string, string?>> Old(CancellationToken token)
        {
            started.SetResult();
            await release.Task;
            return new Dictionary<string, string?> { ["f"] = "old" };
        }

        var old = cache.GetOrAddAsync<string>("k", Old, policy: null, cancellation.Token).AsTask();
        await started.Task;
        var oldRun = generations.Completions().Single();
        await cancellation.CancelAsync();
        await Throws<OperationCanceledException>(old);

        (await cache.GetOrAddAsync<string>("k", _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["f"] = "new" }), policy: null, Ct))["f"].Should().Be("new");
        release.SetResult();
        await oldRun.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        (await cache.GetAsync<string>("k", Ct))!["f"].Should().Be("new");
    }

    [Fact]
    public async Task A_cancelled_batch_whose_generator_ignores_cancellation_keeps_its_keys_until_it_returns()
    {
        using var cache = InMemoryMultilayer.Cache();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGenerated = 0;
        async Task<KeyValuePair<string, string?>[]> Old(string[] states, CancellationToken token)
        {
            started.SetResult();
            await release.Task;
            return states.Select(s => new KeyValuePair<string, string?>(s, "old")).ToArray();
        }

        Task<KeyValuePair<string, string?>[]> Second(string[] states, CancellationToken token)
        {
            Interlocked.Increment(ref secondGenerated);
            return Task.FromResult(states.Select(s => new KeyValuePair<string, string?>(s, "new")).ToArray());
        }

        var first = cache.GetOrAddAsync<string, string>([new("k", "k")], Old, policy: null, cancellation.Token).AsTask();
        await started.Task;
        await cancellation.CancelAsync();
        var second = cache.GetOrAddAsync<string, string>([new("k", "k")], Second, policy: null, Ct).AsTask();
        await Task.Delay(200, Ct);
        secondGenerated.Should().Be(0);
        release.SetResult();

        (await second).Single().Value.Should().BeOneOf("old", "new");
        await first.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    [Fact]
    public async Task A_failed_batch_stops_counting_as_a_waiter_on_the_single_load_it_joined()
    {
        using var cache = InMemoryMultilayer.Cache();
        var generations = ReplaceGenerations(cache, null);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedToken = CancellationToken.None;
        async Task<string?> Load(CancellationToken token)
        {
            sharedToken = token;
            started.SetResult();
            await release.Task.WaitAsync(token);
            return "loaded";
        }

        Task<KeyValuePair<string, string?>[]> Fail(string[] states, CancellationToken token) => throw new InvalidOperationException("batch failed");

        var single = cache.GetOrAddAsync<string>("k", Load, policy: null, cancellation.Token).AsTask();
        await started.Task;
        var batch = cache.GetOrAddAsync<string, string>([new("k", "k"), new("other", "other")], Fail, policy: null, Ct).AsTask();
        await Throws<InvalidOperationException>(batch);
        await cancellation.CancelAsync();
        await Throws<OperationCanceledException>(single);

        sharedToken.IsCancellationRequested.Should().BeTrue();
        generations.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_cancelled_hash_starter_keeps_the_distributed_lease_until_the_shared_load_ends()
    {
        using var cache = InMemoryMultilayer.HashCache(new InMemoryCacheOptions { LocalLockTimeout = TimeSpan.FromMilliseconds(5) });
        var distributed = Substitute.For<IDistributedLock>();
        var lease = new RecordingLease();
        distributed.AcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAsyncDisposable>(lease), new ValueTask<IAsyncDisposable>(NoOpAsyncDisposable.Instance));
        typeof(MultilayerCacheBase).GetField("_distributedLock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cache, distributed);
        var policy = new CachePolicy { Lock = new LockProfile { DistributedLockEnabled = true, LocalLockTimeout = TimeSpan.FromMilliseconds(5) } };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedToken = CancellationToken.None;
        async Task<IDictionary<string, string?>> Load(CancellationToken token)
        {
            sharedToken = token;
            started.SetResult();
            await release.Task.WaitAsync(token);
            return new Dictionary<string, string?> { ["f"] = "loaded" };
        }

        var starter = cache.GetOrAddAsync<string>("k", Load, policy, cancellation.Token).AsTask();
        await started.Task;
        Task<IDictionary<string, string?>> ShouldJoin(CancellationToken token) => throw new InvalidOperationException("should join");
        var joiner = cache.GetOrAddAsync<string>("k", ShouldJoin, policy, Ct).AsTask();
        await Task.Delay(200, Ct);

        await cancellation.CancelAsync();
        await Throws<OperationCanceledException>(starter);
        var releasedWhileRunning = lease.Disposed.Task.IsCompleted;
        release.SetResult();

        (await joiner)["f"].Should().Be("loaded");
        sharedToken.IsCancellationRequested.Should().BeFalse();
        releasedWhileRunning.Should().BeFalse();
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    private static CallbackComparer PauseOnce(ManualResetEventSlim reached, ManualResetEventSlim proceed)
    {
        var calls = 0;
        return new CallbackComparer(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                reached.Set();
                proceed.Wait(TimeSpan.FromSeconds(10), Ct).Should().BeTrue();
            }
        });
    }

    private static ICache MissingInner()
    {
        var inner = Substitute.For<ICache>();
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue }));
        inner.GetCacheEntriesAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<KeyValuePair<CacheKey, ICacheEntry<string?>>[]>(
                call.Arg<CacheKey[]>().Select(k => new KeyValuePair<CacheKey, ICacheEntry<string?>>(k, new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue })).ToArray()));
        inner.SetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(true);
        inner.SetAsync<string>(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(true);
        return inner;
    }

    private static int InnerReads(ICache inner) =>
        inner.ReceivedCalls().Count(c => c.GetMethodInfo().Name.StartsWith("Get", StringComparison.Ordinal));

    private static async Task Throws<TException>(Task task)
        where TException : Exception =>
        await ((Func<Task>)(() => task)).Should().ThrowAsync<TException>();

    private static InFlight<InFlightKey, object?> ReplaceGenerations(MultilayerCache cache, IEqualityComparer<InFlightKey>? comparer)
    {
        var generations = new InFlight<InFlightKey, object?>(comparer);
        typeof(MultilayerCache).GetField("_generations", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cache, generations);
        return generations;
    }

    private sealed class RecordingLease : IAsyncDisposable
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CallbackComparer(Action callback) : IEqualityComparer<InFlightKey>
    {
        public bool Equals(InFlightKey x, InFlightKey y) => x.Equals(y);

        public int GetHashCode(InFlightKey obj)
        {
            callback();
            return obj.GetHashCode();
        }
    }
}
