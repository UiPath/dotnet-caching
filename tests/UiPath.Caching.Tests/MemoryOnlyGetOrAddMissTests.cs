using System.Reflection;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

public class MemoryOnlyGetOrAddMissTests
{
    public static TheoryData<string> Shapes =>
    [
        "key", "key ttl", "span", "span ttl", "state key", "state span", "generated key", "generated span", "generated state key", "generated state span", "result key", "result span", "result state span",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_miss_generates_once_and_keeps_the_value_locally(string shape)
    {
        using var cache = InMemoryMultilayer.Cache();
        var generated = 0;
        Task<string?> Generate(CancellationToken token)
        {
            Interlocked.Increment(ref generated);
            return Task.FromResult<string?>("generated");
        }

        (await Call(shape, cache, "k", Generate, null, Ct)).Should().Be("generated");
        (await Call(shape, cache, "k", Generate, null, Ct)).Should().Be("generated");

        generated.Should().Be(1);
        (await cache.GetAsync<string>("k", Ct)).Should().Be("generated");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_miss_neither_reads_nor_writes_the_inner_cache(string shape)
    {
        using var cache = InMemoryMultilayer.Cache();
        var inner = ReplaceInner(cache);

        (await Call(shape, cache, "k", _ => Task.FromResult<string?>("generated"), null, Ct)).Should().Be("generated");

        inner.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_result_miss_reports_that_it_generated()
    {
        using var cache = InMemoryMultilayer.Cache();

        var first = await cache.GetOrAddResultAsync<string>("k".ToCharArray().AsSpan(), _ => Task.FromResult<string?>("generated"), policy: null, token: Ct);
        var second = await cache.GetOrAddResultAsync<string>("k".ToCharArray().AsSpan(), _ => Task.FromResult<string?>("other"), policy: null, token: Ct);

        first.WasGenerated.Should().BeTrue();
        second.Should().Be(first with { WasGenerated = false });
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Concurrent_misses_share_one_run(string shape)
    {
        using var cache = InMemoryMultilayer.Cache();
        var generated = 0;
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> Generate(CancellationToken token)
        {
            Interlocked.Increment(ref generated);
            return release.Task;
        }

        var calls = Enumerable.Range(0, 8).Select(_ => Call(shape, cache, "k", Generate, null, Ct).AsTask()).ToArray();
        cache.RunsInFlight.Should().Be(1);
        release.SetResult("generated");

        (await Task.WhenAll(calls)).Should().AllBe("generated");
        generated.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_set_after_the_miss_and_before_the_run_is_returned_not_generated(string shape)
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

        var call = Task.Run(async () => await Call(shape, cache, "k", Generate, null, Ct), Ct);
        atReservation.Wait(TimeSpan.FromSeconds(10), Ct).Should().BeTrue();
        (await cache.SetAsync<string>("k", "written", Ct)).Should().BeTrue();
        proceed.Set();

        (await call).Should().Be("written");
        generated.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_remove_after_the_miss_and_before_the_run_still_generates(string shape)
    {
        using var cache = InMemoryMultilayer.Cache();
        using var atReservation = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        ReplaceGenerations(cache, PauseOnce(atReservation, proceed));

        var call = Task.Run(async () => await Call(shape, cache, "k", _ => Task.FromResult<string?>("generated"), null, Ct), Ct);
        atReservation.Wait(TimeSpan.FromSeconds(10), Ct).Should().BeTrue();
        await cache.RemoveAsync<string>("k", Ct);
        proceed.Set();

        (await call).Should().Be("generated");
        (await cache.GetAsync<string>("k", Ct)).Should().Be("generated");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_set_while_the_generator_runs_is_replaced_by_the_generated_value(string shape)
    {
        using var cache = InMemoryMultilayer.Cache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> Generate(CancellationToken token)
        {
            started.SetResult();
            return release.Task;
        }

        var call = Call(shape, cache, "k", Generate, null, Ct).AsTask();
        await started.Task;
        (await cache.SetAsync<string>("k", "written", Ct)).Should().BeTrue();
        (await cache.GetAsync<string>("k", Ct)).Should().Be("written");
        release.SetResult("generated");

        (await call).Should().Be("generated");
        (await cache.GetAsync<string>("k", Ct)).Should().Be("generated");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_remove_while_the_generator_runs_is_followed_by_the_generated_value(string shape)
    {
        using var cache = InMemoryMultilayer.Cache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> Generate(CancellationToken token)
        {
            started.SetResult();
            return release.Task;
        }

        var call = Call(shape, cache, "k", Generate, null, Ct).AsTask();
        await started.Task;
        await cache.RemoveAsync<string>("k", Ct);
        release.SetResult("generated");

        (await call).Should().Be("generated");
        (await cache.GetAsync<string>("k", Ct)).Should().Be("generated");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_cancelled_token_cancels_the_call_and_keeps_nothing(string shape)
    {
        using var cache = InMemoryMultilayer.Cache();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var generated = 0;

        await Throws<OperationCanceledException>(Call(shape, cache, "k", _ => Task.FromResult<string?>($"{Interlocked.Increment(ref generated)}"), null, cts.Token).AsTask());

        generated.Should().Be(0);
        (await cache.GetAsync<string>("k", Ct)).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_token_cancelled_while_the_generator_runs_cancels_the_call_and_keeps_nothing(string shape)
    {
        using var cache = InMemoryMultilayer.Cache();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Generate(CancellationToken token)
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "generated";
        }

        var call = Call(shape, cache, "k", Generate, null, cts.Token).AsTask();
        await started.Task;
        await cts.CancelAsync();

        await Throws<OperationCanceledException>(call);
        (await cache.GetAsync<string>("k", Ct)).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_starter_that_cancels_leaves_the_run_going_for_a_joiner_which_gets_the_value(string shape)
    {
        using var cache = InMemoryMultilayer.Cache();
        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generated = 0;
        async Task<string?> Generate(CancellationToken token)
        {
            Interlocked.Increment(ref generated);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return "generated";
        }

        var starter = Call(shape, cache, "k", Generate, null, leaving.Token).AsTask();
        await started.Task;
        var joiner = Call(shape, cache, "k", Generate, null, Ct).AsTask();
        cache.RunsInFlight.Should().Be(1);
        await leaving.CancelAsync();

        await Throws<OperationCanceledException>(starter);
        release.SetResult();
        (await joiner).Should().Be("generated");
        generated.Should().Be(1);
        (await cache.GetAsync<string>("k", Ct)).Should().Be("generated");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_failing_generator_fails_every_caller_keeps_nothing_and_the_next_call_generates(string shape)
    {
        using var cache = InMemoryMultilayer.Cache();
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var calls = Enumerable.Range(0, 4).Select(_ => Call(shape, cache, "k", _ => release.Task, null, Ct).AsTask()).ToArray();
        release.SetException(new InvalidOperationException("boom"));

        foreach (var call in calls)
        {
            await Throws<InvalidOperationException>(call);
        }

        await Throws<InvalidOperationException>(Call(shape, cache, "k", _ => throw new InvalidOperationException("sync"), null, Ct).AsTask());
        (await cache.GetAsync<string>("k", Ct)).Should().BeNull();
        (await Call(shape, cache, "k", _ => Task.FromResult<string?>("generated"), null, Ct)).Should().Be("generated");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_generator_past_the_factory_timeout_fails_with_a_timeout(string shape)
    {
        using var cache = InMemoryMultilayer.Cache();
        var policy = new CachePolicy { FactoryTimeout = TimeSpan.FromMilliseconds(50) };
        async Task<string?> Generate(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return "generated";
        }

        await Throws<TimeoutException>(Call(shape, cache, "k", Generate, policy, Ct).AsTask());
        (await cache.GetAsync<string>("k", Ct)).Should().BeNull();
    }

    private static ValueTask<string?> Call(string shape, MultilayerCache cache, string key, Func<CancellationToken, Task<string?>> generate, CachePolicy? policy, CancellationToken token) => shape switch
    {
        "key" => cache.GetOrAddAsync<string>((CacheKey)key, generate, policy, token),
        "key ttl" => cache.GetOrAddAsync<string>((CacheKey)key, generate, TimeSpan.FromMinutes(5), policy, token),
        "span" => cache.GetOrAddAsync<string>(key.ToCharArray().AsSpan(), generate, policy, token),
        "span ttl" => cache.GetOrAddAsync<string>(key.ToCharArray().AsSpan(), generate, TimeSpan.FromMinutes(5), policy, token),
        "state key" => cache.GetOrAddAsync<string, Func<CancellationToken, Task<string?>>>((CacheKey)key, generate, static (g, ct) => g(ct), policy, token),
        "state span" => cache.GetOrAddAsync<string, Func<CancellationToken, Task<string?>>>(key.ToCharArray().AsSpan(), generate, static (g, ct) => g(ct), policy, token),
        "generated key" => cache.GetOrAddWithExpirationAsync<string>((CacheKey)key, async ct => new GeneratedValue<string>(await generate(ct), DateTimeOffset.UtcNow.AddMinutes(5)), policy, token),
        "generated state key" => cache.GetOrAddWithExpirationAsync<string, Func<CancellationToken, Task<string?>>>((CacheKey)key, generate, static async (g, ct) => new GeneratedValue<string>(await g(ct), DateTimeOffset.UtcNow.AddMinutes(5)), policy, token),
        "generated span" => cache.GetOrAddWithExpirationAsync<string>(key.ToCharArray().AsSpan(), async ct => new GeneratedValue<string>(await generate(ct), DateTimeOffset.UtcNow.AddMinutes(5)), policy, token),
        "generated state span" => cache.GetOrAddWithExpirationAsync<string, Func<CancellationToken, Task<string?>>>(key.ToCharArray().AsSpan(), generate, static async (g, ct) => new GeneratedValue<string>(await g(ct), DateTimeOffset.UtcNow.AddMinutes(5)), policy, token),
        "result key" => ValueOf(cache.GetOrAddResultAsync<string>((CacheKey)key, generate, policy, token)),
        "result span" => ValueOf(cache.GetOrAddResultAsync<string>(key.ToCharArray().AsSpan(), generate, policy, token)),
        "result state span" => ValueOf(cache.GetOrAddResultAsync<string, Func<CancellationToken, Task<string?>>>(key.ToCharArray().AsSpan(), generate, static (g, ct) => g(ct), policy, token)),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private static async ValueTask<string?> ValueOf(ValueTask<CacheResult<string>> result) => (await result).Value;

    private static async Task Throws<TException>(Task task)
        where TException : Exception =>
        await ((Func<Task>)(() => task)).Should().ThrowAsync<TException>();

    /// <summary>Puts a recording inner cache behind the memory-only cache, which still reports no inner tier, so any read or write of it shows.</summary>
    private static ICache ReplaceInner(MultilayerCache cache)
    {
        var inner = Substitute.For<ICache>();
        typeof(MultilayerCache).GetField("_innerCache", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cache, inner);
        return inner;
    }

    private static void ReplaceGenerations(MultilayerCache cache, IEqualityComparer<InFlightKey> comparer) =>
        typeof(MultilayerCache).GetField("_generations", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cache, new InFlight<InFlightKey, object?>(comparer));

    /// <summary>Holds the first key lookup of the run table, which comes after the caller's local miss and before its run starts.</summary>
    private static PausingComparer PauseOnce(ManualResetEventSlim reached, ManualResetEventSlim proceed) => new(reached, proceed);

    private sealed class PausingComparer(ManualResetEventSlim reached, ManualResetEventSlim proceed) : IEqualityComparer<InFlightKey>
    {
        private int _calls;

        public bool Equals(InFlightKey x, InFlightKey y) => x.Equals(y);

        public int GetHashCode(InFlightKey obj)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                reached.Set();
                proceed.Wait(TimeSpan.FromSeconds(10), Ct).Should().BeTrue();
            }

            return obj.GetHashCode();
        }
    }
}
