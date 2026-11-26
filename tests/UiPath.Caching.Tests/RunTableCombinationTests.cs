using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>Every combination of the in-process shared run (<c>LocalSingleFlightEnabled</c>) and the distributed lock, for a single key, a batch and a hash key: how many generators run and how many times the distributed lock is taken by callers that arrive together.</summary>
public class RunTableCombinationTests
{
    private const int Callers = 8;

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<bool, bool> Combinations() => new()
    {
        { true, false },
        { true, true },
        { false, false },
        { false, true },
    };

    [Theory]
    [MemberData(nameof(Combinations))]
    public async Task A_single_key_miss(bool sharedRun, bool distributedLock)
    {
        var distributed = new RecordingDistributedLock();
        using var cache = InMemoryMultilayer.Cache(distributedLock: distributed, clock: _clock);
        var gate = new Gate(Callers);
        async Task<string?> Generate(CancellationToken _)
        {
            gate.Entered();
            await gate.Released;
            return "generated";
        }

        var calls = Enumerable.Range(0, Callers).Select(_ => cache.GetOrAddAsync<string>("k", Generate, Policy(sharedRun, distributedLock), Ct).AsTask()).ToArray();
        await gate.Open(sharedRun, Ct);
        (await Task.WhenAll(calls)).Should().AllBe("generated");

        gate.Generators.Should().Be(sharedRun ? 1 : Callers);
        distributed.Acquisitions.Should().Be(ExpectedAcquisitions(sharedRun, distributedLock));
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public async Task A_batch_miss(bool sharedRun, bool distributedLock)
    {
        var distributed = new RecordingDistributedLock();
        using var cache = InMemoryMultilayer.Cache(distributedLock: distributed, clock: _clock);
        var gate = new Gate(Callers);
        KeyValuePair<CacheKey, string>[] entries = [new("a", "a"), new("b", "b")];
        async Task<KeyValuePair<string, string?>[]> Generate(string[] states, CancellationToken _)
        {
            gate.Entered();
            await gate.Released;
            return states.Select(x => new KeyValuePair<string, string?>(x, "generated")).ToArray();
        }

        var calls = Enumerable.Range(0, Callers).Select(_ => cache.GetOrAddAsync<string, string>(entries, Generate, Policy(sharedRun, distributedLock), Ct).AsTask()).ToArray();
        await gate.Open(sharedRun, Ct);
        var results = await Task.WhenAll(calls);

        results.Should().AllSatisfy(r => r.Select(p => p.Value).Should().Equal("generated", "generated"));
        gate.Generators.Should().Be(sharedRun ? 1 : Callers);
        distributed.Acquisitions.Should().Be(ExpectedAcquisitions(sharedRun, distributedLock));
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public async Task A_hash_key_miss(bool sharedRun, bool distributedLock)
    {
        var distributed = new RecordingDistributedLock();
        using var cache = InMemoryMultilayer.HashCache(distributedLock: distributed, clock: _clock);
        var gate = new Gate(Callers);
        async Task<IDictionary<string, string?>> Generate(CancellationToken _)
        {
            gate.Entered();
            await gate.Released;
            return new Dictionary<string, string?> { ["f"] = "generated" };
        }

        var calls = Enumerable.Range(0, Callers).Select(_ => cache.GetOrAddAsync<string>("k", Generate, Policy(sharedRun, distributedLock), Ct).AsTask()).ToArray();
        await gate.Open(sharedRun, Ct);
        var results = await Task.WhenAll(calls);

        results.Should().AllSatisfy(r => r["f"].Should().Be("generated"));
        gate.Generators.Should().Be(sharedRun ? 1 : Callers);
        distributed.Acquisitions.Should().Be(ExpectedAcquisitions(sharedRun, distributedLock));
    }

    [Fact]
    public async Task A_joiner_of_a_run_under_a_distributed_lock_never_takes_the_lock()
    {
        var distributed = new RecordingDistributedLock();
        using var cache = InMemoryMultilayer.Cache(distributedLock: distributed, clock: _clock);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Generate(CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            return "generated";
        }

        var policy = Policy(true, true);
        var firstRun = cache.GetOrAddAsync<string>("k", Generate, policy, Ct);
        var first = firstRun.AsTask();
        await started.Task.WaitAsync(Ct);
        var joiner = cache.GetOrAddAsync<string>("k", Generate, policy, Ct).AsTask();
        joiner.IsCompleted.Should().BeFalse();
        distributed.Acquisitions.Should().Be(1, "only the caller that runs the generator takes the lock");
        release.SetResult();

        (await first).Should().Be("generated");
        (await joiner).Should().Be("generated");
        distributed.Acquisitions.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TryAdd_takes_no_distributed_lock_and_one_caller_wins(bool sharedRun)
    {
        var distributed = new RecordingDistributedLock();
        using var cache = InMemoryMultilayer.Cache(distributedLock: distributed, clock: _clock);
        var policy = Policy(sharedRun, false);

        var results = await Task.WhenAll(Enumerable.Range(0, Callers).Select(i => Task.Run(async () => await cache.TryAddAsync<string>("k", "caller-" + i, policy, Ct), Ct)));

        results.Count(won => won).Should().Be(1);
        distributed.Acquisitions.Should().Be(0);
    }

    private static int ExpectedAcquisitions(bool sharedRun, bool distributedLock)
    {
        if (!distributedLock)
        {
            return 0;
        }

        return sharedRun ? 1 : Callers;
    }

    private static CachePolicy Policy(bool sharedRun, bool distributedLock) =>
        new() { Lock = new LockProfile { LocalSingleFlightEnabled = sharedRun, DistributedLockEnabled = distributedLock } };

    private sealed class Gate(int callers)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _generators;

        public Task Released => _release.Task;

        public int Generators => Volatile.Read(ref _generators);

        public void Entered()
        {
            var count = Interlocked.Increment(ref _generators);
            if (count == callers)
            {
                _all.TrySetResult();
            }
            if (count == 1)
            {
                _first.TrySetResult();
            }
        }

        /// <summary>With a shared run one generator starts and the rest join it; without one each caller starts its own.</summary>
        public async Task Open(bool sharedRun, CancellationToken token)
        {
            await (sharedRun ? _first.Task : _all.Task).WaitAsync(token);
            Generators.Should().Be(sharedRun ? 1 : callers);
            _release.TrySetResult();
        }
    }
}
