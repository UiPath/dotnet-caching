using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>Every combination of the in-process shared run (<c>LocalSingleFlightEnabled</c>) and the distributed lock, for a single key, a batch and a hash key: how many generators run and how many times the distributed lock is taken by callers that arrive together.</summary>
public class RunTableCombinationTests
{
    private const int Callers = 8;

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
        using var cache = InMemoryMultilayer.Cache(distributedLock: distributed);
        var gate = new Gate(Callers);
        async Task<string?> Generate(CancellationToken _)
        {
            gate.Entered();
            await gate.Released;
            return "generated";
        }

        var calls = Enumerable.Range(0, Callers).Select(_ => Task.Run(async () => await cache.GetOrAddAsync<string>("k", Generate, Policy(sharedRun, distributedLock), Ct), Ct)).ToArray();
        await gate.Open(sharedRun, Ct);
        (await Task.WhenAll(calls)).Should().AllBe("generated");

        gate.Generators.Should().Be(sharedRun ? 1 : Callers);
        distributed.Acquisitions.Should().Be(!distributedLock ? 0 : sharedRun ? 1 : Callers);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public async Task A_batch_miss(bool sharedRun, bool distributedLock)
    {
        var distributed = new RecordingDistributedLock();
        using var cache = InMemoryMultilayer.Cache(distributedLock: distributed);
        var gate = new Gate(Callers);
        KeyValuePair<CacheKey, string>[] entries = [new("a", "a"), new("b", "b")];
        async Task<KeyValuePair<string, string?>[]> Generate(string[] states, CancellationToken _)
        {
            gate.Entered();
            await gate.Released;
            return states.Select(x => new KeyValuePair<string, string?>(x, "generated")).ToArray();
        }

        var calls = Enumerable.Range(0, Callers).Select(_ => Task.Run(async () => await cache.GetOrAddAsync<string, string>(entries, Generate, Policy(sharedRun, distributedLock), Ct), Ct)).ToArray();
        await gate.Open(sharedRun, Ct);
        var results = await Task.WhenAll(calls);

        results.Should().AllSatisfy(r => r.Select(p => p.Value).Should().Equal("generated", "generated"));
        gate.Generators.Should().Be(sharedRun ? 1 : Callers);
        distributed.Acquisitions.Should().Be(!distributedLock ? 0 : sharedRun ? 1 : Callers);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public async Task A_hash_key_miss(bool sharedRun, bool distributedLock)
    {
        var distributed = new RecordingDistributedLock();
        using var cache = InMemoryMultilayer.HashCache(distributedLock: distributed);
        var gate = new Gate(Callers);
        async Task<IDictionary<string, string?>> Generate(CancellationToken _)
        {
            gate.Entered();
            await gate.Released;
            return new Dictionary<string, string?> { ["f"] = "generated" };
        }

        var calls = Enumerable.Range(0, Callers).Select(_ => Task.Run(async () => await cache.GetOrAddAsync<string>("k", Generate, Policy(sharedRun, distributedLock), Ct), Ct)).ToArray();
        await gate.Open(sharedRun, Ct);
        var results = await Task.WhenAll(calls);

        results.Should().AllSatisfy(r => r["f"].Should().Be("generated"));
        gate.Generators.Should().Be(sharedRun ? 1 : Callers);
        distributed.Acquisitions.Should().Be(!distributedLock ? 0 : sharedRun ? 1 : Callers);
    }

    [Fact]
    public async Task A_joiner_of_a_run_under_a_distributed_lock_never_takes_the_lock()
    {
        var distributed = new RecordingDistributedLock();
        using var cache = InMemoryMultilayer.Cache(distributedLock: distributed);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Generate(CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            return "generated";
        }

        var policy = Policy(true, true);
        var first = cache.GetOrAddAsync<string>("k", Generate, policy, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var joiner = cache.GetOrAddAsync<string>("k", Generate, policy, Ct).AsTask();
        await Task.Delay(100, Ct);
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
        using var cache = InMemoryMultilayer.Cache(distributedLock: distributed);
        var policy = Policy(sharedRun, false);

        var results = await Task.WhenAll(Enumerable.Range(0, Callers).Select(i => Task.Run(async () => await cache.TryAddAsync<string>("k", "caller-" + i, policy, Ct), Ct)));

        results.Count(won => won).Should().Be(1);
        distributed.Acquisitions.Should().Be(0);
    }

    private static CachePolicy Policy(bool sharedRun, bool distributedLock) =>
        new() { Lock = new LockProfile { LocalSingleFlightEnabled = sharedRun, DistributedLockEnabled = distributedLock } };

    private sealed class Gate(int callers)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _generators;

        public Task Released => _release.Task;

        public int Generators => Volatile.Read(ref _generators);

        public void Entered()
        {
            if (Interlocked.Increment(ref _generators) == 1)
            {
                _first.TrySetResult();
            }
        }

        /// <summary>With a shared run one generator starts and the rest join it; without one each caller starts its own.</summary>
        public async Task Open(bool sharedRun, CancellationToken token)
        {
            await _first.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            var deadline = Environment.TickCount64 + 300;
            while (Generators < (sharedRun ? 1 : callers) && Environment.TickCount64 < deadline)
            {
                await Task.Delay(10, token);
            }

            await Task.Delay(sharedRun ? 150 : 0, token);
            _release.TrySetResult();
        }
    }
}
